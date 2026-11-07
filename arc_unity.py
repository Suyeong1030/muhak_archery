#!/usr/bin/env python3
"""EMG(Arduino) + 웹캠 자세추정 -> Unity UDP(JSON) 전송.
좌표계(Unity): x=좌우(+우), y=높이, z=전방, 단위 m.
실행: python arc_unity.py --port COM3 --front 0 --side 1 --host 127.0.0.1 --udp 5005
"""
import argparse
import json
import math
import re
import socket
import sys
import threading
import time
from collections import deque

import cv2
import numpy as np

try:
    import serial
    import serial.tools.list_ports
except ImportError:
    serial = None

try:
    import mediapipe as mp
except ImportError:
    mp = None

COUNTDOWN_SEC = 3.0
HOLD_SEC = 3.0
HOLD_TOL = 0.075
MIN_EXTENSION = 0.16
CALIB_TIMEOUT_SEC = 90.0

ARM_EMG_PCT = 50.0
ARM_MP_PCT = 45.0
ARM_FUSED_PCT = 45.0
ARM_FRAMES = 3
RELEASE_WINDOW_SEC = 0.55
RELEASE_MIN_RATE = 65.0
RELEASE_GRACE_SEC = 0.50
MIN_ARM_AT_RELEASE = 25.0
MIN_HAND_HEIGHT = 0.18
COOLDOWN_SEC = 1.15
MIN_HAND_SEP = 0.10
MAX_DRAW_CM = 70.0
MAX_TRAJ_POINTS = 150


class Calibration:
    def __init__(self):
        self.ok = False
        self.span_min = 0.08
        self.span_max = 0.90
        self.curve = np.zeros(101)

    @property
    def rest_emg(self):
        return float(self.curve[0]) if self.ok else 0.0

    def build(self, pairs):
        groups = {}
        for p, x in pairs:
            groups.setdefault(int(round(min(max(p, 0.0), 100.0))), []).append(x)
        ps = sorted(groups)
        xs = [float(np.median(groups[p])) for p in ps]
        if ps[0] > 0:
            ps.insert(0, 0)
            xs.insert(0, xs[0])
        if ps[-1] < 100:
            ps.append(100)
            xs.append(xs[-1])
        xs = np.maximum.accumulate(xs)
        curve = np.interp(np.arange(101), ps, xs)
        step = max(curve[-1] - curve[0], 0.0) / 500.0
        for i in range(1, 101):
            curve[i] = max(curve[i], curve[i - 1] + step)
        self.curve = curve
        self.ok = True

    def emg_to_pct(self, x):
        if not self.ok or x <= self.curve[0]:
            return 0.0
        if x >= self.curve[-1]:
            return 100.0
        return float(round(np.interp(x, self.curve, np.arange(101))))

    def save(self, path):
        with open(path, "w") as f:
            json.dump({"span_min": self.span_min, "span_max": self.span_max, "curve": self.curve.tolist()}, f)

    @classmethod
    def load(cls, path):
        cal = cls()
        with open(path) as f:
            d = json.load(f)
        cal.span_min, cal.span_max = d["span_min"], d["span_max"]
        cal.curve = np.array(d["curve"], dtype=float)
        cal.ok = True
        return cal


class EMGReader:
    NUM = re.compile(r"[-+]?\d*\.?\d+")

    def __init__(self, port=None, baud=115200):
        self.value = 0.0
        self.last_rx = 0.0
        self.lock = threading.Lock()
        self.ser = None
        if serial is None:
            print("[EMG] pyserial 없음 - EMG 비활성")
            return
        port = port or self._find_port()
        if not port:
            print("[EMG] 포트를 찾지 못함 - EMG 비활성")
            return
        try:
            self.ser = serial.Serial(port, baud, timeout=0.05)
        except Exception as e:
            print(f"[EMG] {port} 열기 실패: {e}")
            return
        time.sleep(2.0)
        print(f"[EMG] 연결: {port} @ {baud}")
        threading.Thread(target=self._loop, daemon=True).start()

    @staticmethod
    def _find_port():
        keys = ("arduino", "usb", "ch340", "cp210", "ftdi", "wch", "acm")
        for p in serial.tools.list_ports.comports():
            if any(k in f"{p.device} {p.description} {p.manufacturer}".lower() for k in keys):
                return p.device
        return None

    def _loop(self):
        buf = ""
        while True:
            try:
                data = self.ser.read(max(1, self.ser.in_waiting))
            except Exception:
                self.ser = None
                return
            if not data:
                continue
            buf = (buf + data.decode("utf-8", errors="ignore"))[-4096:]
            if "\n" not in buf:
                continue
            lines = re.split(r"[\r\n]+", buf)
            buf = lines[-1]
            for line in reversed(lines[:-1]):
                nums = self.NUM.findall(line)
                if nums:
                    with self.lock:
                        self.value = float(nums[-1])
                        self.last_rx = time.time()
                    break

    def sample(self):
        with self.lock:
            return self.value, (time.time() - self.last_rx) < 2.5


class EMGProcessor:
    def __init__(self):
        self.raw = 0.0
        self.fast = None
        self.smooth = None
        self.fast_pct = 0.0
        self.est_pct = 0.0
        self.rate = 0.0
        self.low_frames = 0

    @property
    def in_release_zone(self):
        return self.low_frames >= 1

    def update(self, raw, dt, cal):
        dt = max(dt, 1e-3)
        self.raw = raw = max(0.0, raw)
        if self.fast is None:
            self.fast = self.smooth = raw
        else:
            self.fast += min(1.0, dt / (0.008 if raw <= self.fast else 0.016)) * (raw - self.fast)
            self.smooth += min(1.0, dt / (0.028 if raw >= self.smooth else 0.015)) * (raw - self.smooth)
        prev = self.fast_pct
        self.fast_pct = cal.emg_to_pct(self.fast)
        self.est_pct = cal.emg_to_pct(self.smooth)
        self.rate = (self.fast_pct - prev) / dt
        base = cal.rest_emg
        at_zero = (cal.ok and (self.fast_pct <= 0 or raw <= base or self.fast <= base)) or (not cal.ok and raw <= 0)
        self.low_frames = self.low_frames + 1 if at_zero else 0


class Camera:
    def __init__(self, idx):
        self.cap = cv2.VideoCapture(idx, cv2.CAP_DSHOW) if sys.platform == "win32" else cv2.VideoCapture(idx)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
        self.frame = None
        self.lock = threading.Lock()
        self.running = self.cap.isOpened()
        if self.running:
            threading.Thread(target=self._loop, daemon=True).start()
        else:
            print(f"[CAM {idx}] 열기 실패")

    def _loop(self):
        while self.running:
            ok, f = self.cap.read()
            if ok:
                with self.lock:
                    self.frame = f
            else:
                time.sleep(0.01)

    def read(self):
        with self.lock:
            return None if self.frame is None else self.frame.copy()

    def release(self):
        self.running = False
        time.sleep(0.05)
        self.cap.release()


JOINTS = ("LEFT_SHOULDER", "RIGHT_SHOULDER", "LEFT_ELBOW", "RIGHT_ELBOW",
          "LEFT_WRIST", "RIGHT_WRIST", "LEFT_HIP", "RIGHT_HIP")


class Tracker:
    def __init__(self, mirrored=True, noise_floor=0.0032, alpha_min=0.25, alpha_max=0.85):
        self.pose = mp.solutions.pose.Pose(
            model_complexity=1, smooth_landmarks=True,
            min_detection_confidence=0.58, min_tracking_confidence=0.58,
        )
        self.mirrored = mirrored
        self.noise, self.a_min, self.a_max = noise_floor, alpha_min, alpha_max
        self.prev = {}

    def _stabilize(self, key, pt):
        prev = self.prev.get(key)
        if prev is None:
            self.prev[key] = pt
            return pt
        d = [pt[i] - prev[i] for i in range(3)]
        dist = math.sqrt(sum(v * v for v in d))
        if dist < self.noise:
            return prev
        a = self.a_min + (self.a_max - self.a_min) * min(1.0, (dist - self.noise) / 0.035)
        out = tuple(prev[i] + a * d[i] for i in range(3))
        self.prev[key] = out
        return out

    def process(self, frame):
        if frame is None:
            return {}, 1.0
        if self.mirrored:
            frame = cv2.flip(frame, 1)
        h, w = frame.shape[:2]
        aspect = w / max(h, 1)
        small = cv2.resize(frame, (480, max(1, int(h * 480 / w)))) if w > 480 else frame
        res = self.pose.process(cv2.cvtColor(small, cv2.COLOR_BGR2RGB))
        if not res.pose_landmarks:
            return {}, aspect

        PL = mp.solutions.pose.PoseLandmark
        swap = {"LEFT": "RIGHT", "RIGHT": "LEFT"}

        def lm_of(key):
            if self.mirrored:
                side, part = key.split("_", 1)
                key = f"{swap[side]}_{part}"
            return res.pose_landmarks.landmark[PL[key]]

        lh, rh = lm_of("LEFT_HIP"), lm_of("RIGHT_HIP")
        ls, rs = lm_of("LEFT_SHOULDER"), lm_of("RIGHT_SHOULDER")
        if ls.visibility <= 0.40 and rs.visibility <= 0.40:
            return {}, aspect

        sx, sy, sz = (ls.x + rs.x) / 2, (ls.y + rs.y) / 2, (ls.z + rs.z) / 2
        sh_span = math.hypot(rs.x - ls.x, rs.y - ls.y)
        if lh.visibility > 0.45 and rh.visibility > 0.45:
            hx, hy, hz = (lh.x + rh.x) / 2, (lh.y + rh.y) / 2, (lh.z + rh.z) / 2
            spine = math.hypot(sx - hx, sy - hy)
        else:
            spine = max(sh_span * 1.35, 0.32)
            hx, hy, hz = sx, min(0.95, sy + spine), sz
        scale = max(spine, sh_span * 1.25, 0.28)

        coords = {}
        for key in JOINTS:
            j = lm_of(key)
            if j.visibility < 0.35:
                continue
            coords[key] = self._stabilize(key, ((j.x - hx) / scale, (hy - j.y) / scale, (j.z - hz) / scale))
        return coords, aspect


class Filter:
    def __init__(self, deadband, max_slew, speed):
        self.deadband, self.max_slew, self.speed = deadband, max_slew, speed
        self.val = None

    def update(self, target, dt):
        if self.val is None:
            self.val = float(target)
            return self.val
        diff = target - self.val
        if abs(diff) < self.deadband:
            return self.val
        step = diff * min(1.0, dt * self.speed)
        limit = max(self.max_slew * dt, 0.05)
        self.val += float(np.clip(step, -limit, limit))
        return self.val


def hands(c):
    if not all(k in c for k in ("LEFT_WRIST", "RIGHT_WRIST", "LEFT_SHOULDER", "RIGHT_SHOULDER")):
        return None
    lw, rw, ls, rs = c["LEFT_WRIST"], c["RIGHT_WRIST"], c["LEFT_SHOULDER"], c["RIGHT_SHOULDER"]
    cx, cy = (ls[0] + rs[0]) / 2, (ls[1] + rs[1]) / 2
    width = math.hypot(rs[0] - ls[0], rs[1] - ls[1])
    dl = math.hypot(lw[0] - cx, lw[1] - cy)
    dr = math.hypot(rw[0] - cx, rw[1] - cy)
    if width < 0.28 and dr > dl + 0.25:
        return rw, lw
    return lw, rw


def arm_ratio(c):
    h = hands(c)
    if h is None:
        return None
    bow, draw = h
    ls, rs = c["LEFT_SHOULDER"], c["RIGHT_SHOULDER"]
    dx, dy = rs[0] - ls[0], rs[1] - ls[1]
    w = math.hypot(dx, dy)
    if w > 0.15:
        ax, ay = rs[0] + dx / w * 0.15 * w, rs[1] + dy / w * 0.15 * w
    else:
        ex, ey = rs[0] - bow[0], rs[1] - bow[1]
        n = max(math.hypot(ex, ey), 1e-4)
        ax, ay = rs[0] + ex / n * 0.12, rs[1] + ey / n * 0.12
    bax, bay = ax - bow[0], ay - bow[1]
    ba_len = math.hypot(bax, bay)
    eff = max(ba_len, max(w * 0.95, 0.55))
    bdx, bdy = draw[0] - bow[0], draw[1] - bow[1]
    proj = (bdx * bax + bdy * bay) / ba_len if ba_len > 0.15 else math.hypot(bdx, bdy)
    return proj / max(eff, 0.45)


def mp_draw_pct(c, cal):
    r = arm_ratio(c)
    if r is None:
        return None
    return float(np.clip(round((r - cal.span_min) / max(cal.span_max - cal.span_min, 0.20) * 100.0), 0, 100))


def elevation_deg(c, aspect):
    h = hands(c)
    if h is None:
        if "LEFT_WRIST" not in c or "RIGHT_WRIST" not in c:
            return None
        h = (c["LEFT_WRIST"], c["RIGHT_WRIST"])
    tip, draw = h
    dx, dy = (tip[0] - draw[0]) * aspect, tip[1] - draw[1]
    if math.hypot(dx, dy) < MIN_HAND_SEP:
        return None
    dy = 0.0 if abs(dy) < 0.045 else math.copysign(abs(dy) - 0.045, dy)
    ang = math.degrees(math.atan2(dy, max(abs(dx), 0.35))) * 0.18
    return float(np.clip(ang, -5.0, 5.0))


def azimuth_deg(c, depth):
    if not all(k in c for k in ("LEFT_WRIST", "RIGHT_WRIST", "LEFT_SHOULDER", "RIGHT_SHOULDER")):
        return None
    lw, rw, ls, rs = c["LEFT_WRIST"], c["RIGHT_WRIST"], c["LEFT_SHOULDER"], c["RIGHT_SHOULDER"]
    neutral = ls[0] - 0.45 * abs(rs[0] - ls[0])
    shift = (lw[0] - neutral) * 0.65 + ((lw[0] + rw[0]) / 2 - (ls[0] + rs[0]) / 2) * 0.35
    eff = max(1.10, depth * 1.50) if depth is not None else 1.50
    if abs(shift) < 0.04:
        shift = 0.0
    return float(np.clip(math.degrees(math.atan2(shift * 0.25, eff)), -6.0, 6.0))


def analyze(front, f_aspect, side, s_aspect, cal):
    has_f = "LEFT_WRIST" in front and "RIGHT_WRIST" in front
    has_s = "LEFT_WRIST" in side and "RIGHT_WRIST" in side
    f_draw = mp_draw_pct(front, cal) if has_f else None
    s_draw = mp_draw_pct(side, cal) if has_s else None

    depth = None
    if has_s:
        h = hands(side)
        if h is not None:
            depth = abs(h[0][0] - h[1][0]) * s_aspect

    e_s = elevation_deg(side, s_aspect) if has_s else None
    e_f = elevation_deg(front, f_aspect) if has_f else None
    if e_s is not None and e_f is not None:
        elev = 0.8 * e_s + 0.2 * e_f
    else:
        elev = e_s if e_s is not None else e_f

    azim = azimuth_deg(front, depth) if has_f else (azimuth_deg(side, depth) if has_s else None)

    if f_draw is not None and s_draw is not None:
        draw = float(np.clip(round(0.55 * f_draw + 0.45 * s_draw), 0, 100))
    else:
        draw = f_draw if f_draw is not None else s_draw
    return draw, elev, azim


def raw_span(front, side):
    f = arm_ratio(front) if front else None
    s = arm_ratio(side) if side else None
    if f is not None and s is not None:
        return 0.55 * f + 0.45 * s
    return f if f is not None else s


def fuse_draw(mp_p, emg_p, has_emg):
    mp_p, emg_p = float(np.clip(mp_p, 0, 100)), float(np.clip(emg_p, 0, 100))
    if not has_emg:
        return float(round(mp_p))
    if mp_p < 1.0 or emg_p < 1.0:
        return 0.0
    diff = abs(mp_p - emg_p)
    gate = min(1.0, (min(mp_p, emg_p) / 5.0) ** 1.05)
    w = float(np.clip(1.0 - diff / 50.0, 0.40, 0.85))
    fused = gate * (w * (mp_p + emg_p) / 2 + (1 - w) * math.sqrt(mp_p * emg_p))
    return float(np.clip(round(fused), 0, 100))


def ballistics(draw_pct, elev_deg, azim_deg, launch_h=1.50, dt=0.015):
    empty = dict(pts=[(0.0, launch_h, 0.0)], dist=0.0, lateral=0.0, max_h=launch_h, force=0.0,
                 speed=0.0, apex=0.0, time=0.0, impact=0.0)
    if draw_pct < 0.5:
        return empty

    g = 9.81
    f = float(np.clip(draw_pct / 100.0, 0.01, 1.0))
    k = (f * f + 2.0 * f) / 3.0
    force = round(210.0 * k, 2)
    v0 = round(2.5 + 67.5 * k ** 1.15, 2)

    eff_elev = float(np.clip(elev_deg * 0.15, -2.2, 2.2)) - 4.5 * (1.0 - k) ** 2.2
    th = math.radians(eff_elev)
    ps = math.radians(float(np.clip(azim_deg * 0.25, -5.0, 5.0)))
    vx = v0 * math.cos(th) * math.cos(ps)
    vy = v0 * math.sin(th)
    vz = v0 * math.cos(th) * math.sin(ps)

    gravity = g * (0.022 + 1.15 * (1.0 - k) ** 1.85)
    drag = 0.0005
    x, y, z = 0.0, launch_h, 0.0
    max_y, apex_x, t = y, 0.0, 0.0
    pts = [(x, y, z)]

    for _ in range(1600):
        v = math.sqrt(vx * vx + vy * vy + vz * vz)
        vx += -drag * v * vx * dt
        vy += (-gravity - drag * v * vy) * dt
        vz += -drag * v * vz * dt
        x += vx * dt
        y += vy * dt
        z += vz * dt
        t += dt
        if y > max_y:
            max_y, apex_x = y, x
        if x >= 75.0:
            pts.append((75.0, max(0.0, y), z))
            break
        if y <= 0.0:
            px, py, pz = pts[-1]
            fr = py / max(py - y, 1e-6)
            pts.append((px + (x - px) * fr, 0.0, pz + (z - pz) * fr))
            t = t - dt + dt * fr
            break
        pts.append((x, y, z))

    return dict(
        pts=pts, dist=round(pts[-1][0], 2), lateral=round(pts[-1][2], 2), max_h=round(max_y, 2),
        force=force, speed=v0, apex=round(apex_x, 2), time=round(t, 2),
        impact=round(abs(math.degrees(math.atan2(vy, max(math.hypot(vx, vz), 1e-4)))), 1),
    )


def ring_score(lateral, elev_deg, draw_pct):
    if draw_pct < 5.0:
        return 0
    penalty = max(0.0, (85.0 - draw_pct) * 0.05)
    err = math.hypot(lateral * 0.45, (elev_deg - 4.0) * 0.08 + penalty)
    return int(round(np.clip(10.5 - err * 2.1, 1.0, 10.0)))


def to_unity(pts):
    step = max(1, len(pts) // MAX_TRAJ_POINTS)
    sel = pts[::step]
    if sel[-1] != pts[-1]:
        sel.append(pts[-1])
    return [{"x": round(p[2], 3), "y": round(p[1], 3), "z": round(p[0], 3)} for p in sel]


class UnitySender:
    def __init__(self, host, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.addr = (host, port)

    def send(self, payload):
        try:
            self.sock.sendto(json.dumps(payload, separators=(",", ":")).encode("utf-8"), self.addr)
        except OSError:
            pass


class ShotDetector:
    def __init__(self, rest_emg=0.0):
        self.rest_emg = rest_emg
        self.shot_id = 0
        self.phase = "IDLE"
        self.armed_after_cooldown = True
        self.since_release = 1e6
        self.rearm_low = 0
        self._clear()

    def _clear(self):
        self.contracted = False
        self.frames = 0
        self.peak_draw = 0.0
        self.peak_emg = 0.0
        self.peak_rate = 0.0
        self.last_active = 0.0
        self.snap = None

    @staticmethod
    def anchor_ok(arm_pct, front, side):
        if arm_pct < MIN_ARM_AT_RELEASE:
            return False
        c = front or side
        h = hands(c) if c else None
        return not (h is not None and max(h[0][1], h[1][1]) < MIN_HAND_HEIGHT)

    def update(self, s, emg, front, side, now, dt):
        self.since_release += dt

        if not self.armed_after_cooldown:
            self.phase = "RELOAD"
            self.rearm_low = self.rearm_low + 1 if emg.in_release_zone else 0
            if self.since_release >= COOLDOWN_SEC and self.rearm_low >= 2:
                self.armed_after_cooldown = True
                self.phase = "IDLE"
                self._clear()
            return None

        active = max(emg.est_pct, emg.fast_pct)
        qualifies = (active >= ARM_EMG_PCT and s["arm"] >= ARM_MP_PCT
                     and max(s["draw"], s["fused_raw"]) >= ARM_FUSED_PCT)

        if qualifies:
            self.frames += 1
            self.peak_draw = max(self.peak_draw, s["draw"])
            self.peak_emg = max(self.peak_emg, active)
            self.peak_rate = 0.0
            if self.snap is None or s["draw"] >= self.snap["draw"]:
                self.snap = dict(draw=s["draw"], mp=s["arm"], emg=emg.est_pct, raw=emg.raw,
                                 elev=s["elev"], azim=s["azim"])
            if self.frames >= ARM_FRAMES:
                self.last_active = now
                self.contracted = True
                self.phase = "ARMED"
            else:
                self.phase = "DRAWING"
            return None

        if not self.contracted:
            if emg.in_release_zone or s["arm"] < 15.0:
                self._clear()
                self.phase = "IDLE"
            elif s["arm"] >= 18.0 or emg.est_pct >= 15.0:
                self.phase = "DRAWING"
                self.frames = max(0, self.frames - 1)
            return None

        self.peak_rate = min(self.peak_rate, emg.rate)
        since_peak = now - self.last_active
        anchor = self.anchor_ok(s["arm"], front, side)
        reached_zero = emg.in_release_zone and (
            emg.fast_pct <= 0 or emg.raw <= self.rest_emg or emg.fast <= self.rest_emg)

        if not anchor and not emg.in_release_zone:
            return self._cancel()
        if not reached_zero:
            if since_peak > RELEASE_GRACE_SEC and emg.est_pct < 35.0:
                return self._cancel()
            return None
        if not anchor:
            return self._cancel()
        if not (since_peak <= RELEASE_WINDOW_SEC or abs(self.peak_rate) >= RELEASE_MIN_RATE):
            return self._cancel()
        return self._fire(s, emg)

    def _cancel(self):
        self._clear()
        self.phase = "IDLE"
        return None

    def _fire(self, s, emg):
        snap = self.snap or dict(draw=s["draw"], mp=s["arm"], emg=emg.est_pct, raw=emg.raw,
                                 elev=s["elev"], azim=s["azim"])
        draw = max(1.0, float(round(max(snap["draw"], self.peak_draw))))
        b = ballistics(draw, snap["elev"], snap["azim"], dt=0.012)
        self.shot_id += 1
        shot = {
            "type": "shot",
            "shot_id": self.shot_id,
            "t": time.time(),
            "draw_pct": draw,
            "mp_draw_pct": snap["mp"],
            "emg_pct": snap["emg"],
            "emg_raw": round(snap["raw"], 1),
            "release_pct": emg.fast_pct,
            "draw_cm": round(draw / 100.0 * MAX_DRAW_CM, 2),
            "force_n": b["force"],
            "speed_ms": b["speed"],
            "elev_deg": round(snap["elev"], 2),
            "azim_deg": round(snap["azim"], 2),
            "dist_m": b["dist"],
            "lateral_m": b["lateral"],
            "max_h_m": b["max_h"],
            "apex_m": b["apex"],
            "flight_s": b["time"],
            "impact_deg": b["impact"],
            "score": ring_score(b["lateral"], snap["elev"], draw),
            "traj": to_unity(b["pts"]),
        }
        self.armed_after_cooldown = False
        self.since_release = 0.0
        self.rearm_low = 0
        self._clear()
        self.phase = "RELOAD"
        return shot


def read_pose(cams, trackers):
    out = []
    for cam, tr in zip(cams, trackers):
        if cam is None:
            out.append(({}, 1.0))
        else:
            out.append(tr.process(cam.read()))
    return out


def finalize_calibration(cal, trace, rest_spans, rest_emgs, hold_spans, hold_emgs):
    if rest_spans:
        cal.span_min = round(float(np.median(rest_spans)), 4)
    elif trace:
        cal.span_min = round(float(min(s for s, _ in trace)), 4)
    if hold_spans:
        cal.span_max = round(max(float(np.median(hold_spans)), cal.span_min + 0.15), 4)
    elif trace:
        cal.span_max = round(max(max(s for s, _ in trace), cal.span_min + 0.15), 4)
    rng = max(cal.span_max - cal.span_min, 1e-4)
    pairs = []
    if rest_emgs:
        pairs.append((0.0, float(np.median(rest_emgs))))
    pairs += [(float(np.clip((s - cal.span_min) / rng * 100.0, 0, 100)), x) for s, x in trace]
    if hold_emgs:
        pairs.append((100.0, float(np.median(hold_emgs))))
    if pairs:
        cal.build(pairs)


def calibrate(cams, trackers, emg, sender):
    cal = Calibration()
    sender.send({"type": "calib", "stage": "wait_ready"})
    input("준비되면 Enter -> 3초 카운트다운 후 측정 시작 (활을 최대로 당겨 3초간 유지) ")

    t0 = time.time()
    while (rem := COUNTDOWN_SEC - (time.time() - t0)) > 0:
        sender.send({"type": "calib", "stage": "countdown", "count": int(math.ceil(rem))})
        print(f"  {int(math.ceil(rem))}...")
        time.sleep(min(1.0, rem))

    trace, rest_spans, rest_emgs, hold_spans, hold_emgs = [], [], [], [], []
    recent = deque(maxlen=12)
    min_span, max_span = 0.12, 0.55
    anchor, hold_t = None, 0.0
    prev, start = time.time(), time.time()

    while time.time() - start < CALIB_TIMEOUT_SEC:
        now = time.time()
        dt = max(now - prev, 1e-3)
        prev = now
        (front, _), (side, _) = read_pose(cams, trackers)
        span = raw_span(front, side)
        if span is None:
            continue
        raw, live = emg.sample()

        if len(rest_spans) < 8 or span <= min_span + 0.03:
            min_span = min(min_span, span) if rest_spans else span
            rest_spans.append(span)
            if live:
                rest_emgs.append(raw)
        max_span = max(max_span, span)
        recent.append(span)
        if live:
            trace.append((span, raw))

        far = span >= min_span + MIN_EXTENSION and span >= max_span - HOLD_TOL
        still = len(recent) < 4 or (max(recent) - min(recent)) <= HOLD_TOL
        if far and still:
            if anchor is None:
                anchor = span
            if abs(span - anchor) <= HOLD_TOL:
                hold_t = min(HOLD_SEC, hold_t + dt)
                hold_spans.append(span)
                if live:
                    hold_emgs.append(raw)
                if hold_t >= HOLD_SEC:
                    break
            else:
                anchor = span
                hold_spans.clear()
                hold_emgs.clear()
                hold_t = max(0.0, hold_t - dt * 1.5)
        else:
            anchor = None
            hold_spans.clear()
            hold_emgs.clear()
            hold_t = max(0.0, hold_t - dt * 1.5)

        sender.send({"type": "calib", "stage": "measuring", "span": round(span, 3),
                     "emg_raw": raw, "hold_progress": round(hold_t / HOLD_SEC, 2)})

    finalize_calibration(cal, trace, rest_spans, rest_emgs, hold_spans, hold_emgs)
    sender.send({"type": "calib", "stage": "done", "ok": cal.ok})
    if cal.ok:
        print(f"[CALIB] 완료 span {cal.span_min:.3f}~{cal.span_max:.3f}, EMG {cal.curve[0]:.0f}~{cal.curve[-1]:.0f}")
    else:
        print("[CALIB] EMG 데이터가 없어 보정 실패 - 발사 판정 불가(자세 기반 값만 전송)")
    return cal


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", default=None)
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--front", type=int, default=0)
    ap.add_argument("--side", type=int, default=1)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--udp", type=int, default=5005)
    ap.add_argument("--rate", type=float, default=30.0)
    ap.add_argument("--calib", default="calib.json")
    ap.add_argument("--recalibrate", action="store_true")
    args = ap.parse_args()

    if mp is None:
        sys.exit("mediapipe 가 필요합니다: pip install mediapipe")

    cams = [Camera(args.front), Camera(args.side) if args.side >= 0 else None]
    cams = [c if (c and c.running) else None for c in cams]
    trackers = [Tracker(), Tracker()]
    emg = EMGReader(args.port, args.baud)
    sender = UnitySender(args.host, args.udp)

    try:
        try:
            if args.recalibrate:
                raise FileNotFoundError
            cal = Calibration.load(args.calib)
            print(f"[CALIB] {args.calib} 불러옴")
        except (FileNotFoundError, KeyError, ValueError):
            cal = calibrate(cams, trackers, emg, sender)
            if cal.ok:
                cal.save(args.calib)

        proc, det = EMGProcessor(), ShotDetector(cal.rest_emg)
        f_arm, f_draw = Filter(0.02, 360, 28), Filter(0.02, 360, 28)
        f_elev, f_azim = Filter(1.20, 10, 2.8), Filter(1.00, 10, 2.8)
        elev = azim = 0.0
        preview_key, preview = None, ballistics(0, 0, 0)
        interval = 1.0 / max(args.rate, 1.0)
        prev = last_send = time.time()
        print(f"[RUN] UDP -> {args.host}:{args.udp}  (Ctrl+C 종료)")

        while True:
            now = time.time()
            dt = max(now - prev, 1e-3)
            prev = now

            (front, fa), (side, sa) = read_pose(cams, trackers)
            raw, live = emg.sample()
            proc.update(raw, dt, cal)

            m_draw, m_elev, m_azim = analyze(front, fa, side, sa, cal)
            arm = float(np.clip(round(f_arm.update(m_draw if m_draw is not None else 0.0, dt)), 0, 100))
            if m_elev is not None:
                elev = round(f_elev.update(m_elev, dt), 2)
            if m_azim is not None:
                azim = round(f_azim.update(m_azim, dt), 2)

            est = float(round(proc.est_pct))
            fused_raw = fuse_draw(arm, est, live)
            draw = float(np.clip(round(f_draw.update(fused_raw, dt)), 0, 100))

            key = (int(draw), int(round(elev / 2.0)), int(round(azim / 2.0)))
            if key != preview_key:
                preview, preview_key = ballistics(draw, elev, azim, dt=0.02), key

            state = dict(draw=draw, arm=arm, fused_raw=fused_raw, elev=elev, azim=azim)
            shot = det.update(state, proc, front, side, now, dt)
            if shot:
                sender.send(shot)
                print(f"[SHOT #{shot['shot_id']}] draw {shot['draw_pct']:.0f}% "
                      f"{shot['force_n']:.0f}N {shot['dist_m']:.1f}m score {shot['score']}")

            if now - last_send >= interval:
                last_send = now
                sender.send({
                    "type": "frame", "t": now, "phase": det.phase,
                    "draw_pct": draw, "mp_draw_pct": arm, "emg_pct": est,
                    "emg_raw": round(proc.raw, 1), "emg_live": live,
                    "draw_cm": round(draw / 100.0 * MAX_DRAW_CM, 2),
                    "force_n": preview["force"], "speed_ms": preview["speed"],
                    "elev_deg": elev, "azim_deg": azim,
                    "dist_m": preview["dist"], "lateral_m": preview["lateral"],
                    "max_h_m": preview["max_h"],
                    "cam_front": bool(front), "cam_side": bool(side),
                })
    except KeyboardInterrupt:
        pass
    finally:
        for c in cams:
            if c:
                c.release()


if __name__ == "__main__":
    main()
