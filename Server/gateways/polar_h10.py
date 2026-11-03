"""Stream Polar H10 heart-rate measurements into the Artemis API."""

from __future__ import annotations

import argparse
import asyncio
from contextlib import suppress
from dataclasses import dataclass
from datetime import datetime, timezone
import logging
from typing import Any


HEART_RATE_MEASUREMENT_UUID = "00002a37-0000-1000-8000-00805f9b34fb"
POLAR_NAME = "polar h10"
LOGGER = logging.getLogger("artemis.polar_h10")


@dataclass(frozen=True)
class HeartRateMeasurement:
    heart_rate_bpm: int
    rr_intervals_ms: list[float]


@dataclass(frozen=True)
class GatewaySample:
    session_id: str
    device_id: str
    captured_at: datetime
    heart_rate_bpm: int
    rr_intervals_ms: list[float]

    def as_json(self) -> dict[str, Any]:
        return {
            "session_id": self.session_id,
            "device_id": self.device_id,
            "captured_at": self.captured_at.isoformat(),
            "heart_rate_bpm": self.heart_rate_bpm,
            "rr_intervals_ms": self.rr_intervals_ms,
        }


def parse_heart_rate_measurement(data: bytes | bytearray) -> HeartRateMeasurement:
    """Parse Bluetooth SIG Heart Rate Measurement characteristic (0x2A37)."""
    if len(data) < 2:
        raise ValueError("heart-rate packet is too short")

    flags = data[0]
    offset = 1
    if flags & 0x01:
        if len(data) < offset + 2:
            raise ValueError("packet is missing the 16-bit heart-rate value")
        heart_rate = int.from_bytes(data[offset : offset + 2], "little")
        offset += 2
    else:
        heart_rate = data[offset]
        offset += 1

    if flags & 0x08:  # Energy Expended is present but is not sent to Artemis yet.
        if len(data) < offset + 2:
            raise ValueError("packet is missing the energy-expended value")
        offset += 2

    rr_intervals: list[float] = []
    if flags & 0x10:
        remaining = len(data) - offset
        if remaining % 2:
            raise ValueError("packet has an incomplete RR interval")
        while offset < len(data):
            rr_raw = int.from_bytes(data[offset : offset + 2], "little")
            rr_intervals.append(round(rr_raw * 1000 / 1024, 3))
            offset += 2

    return HeartRateMeasurement(heart_rate, rr_intervals)


def _put_latest(queue: asyncio.Queue[GatewaySample], sample: GatewaySample) -> None:
    if queue.full():
        with suppress(asyncio.QueueEmpty):
            queue.get_nowait()
            queue.task_done()
        LOGGER.warning("전송 대기열이 가득 차 가장 오래된 샘플을 제거했습니다.")
    queue.put_nowait(sample)


async def _send_samples(
    queue: asyncio.Queue[GatewaySample],
    endpoint: str,
) -> None:
    try:
        import httpx
    except ImportError as exc:  # pragma: no cover - depends on local installation
        raise RuntimeError("gateway 의존성이 없습니다: pip install -e '.[gateway]'") from exc

    async with httpx.AsyncClient(timeout=5.0) as client:
        while True:
            sample = await queue.get()
            retry_delay = 1.0
            try:
                while True:
                    try:
                        response = await client.post(endpoint, json=sample.as_json())
                        if 400 <= response.status_code < 500:
                            LOGGER.error(
                                "서버가 샘플을 거부했습니다 (%s): %s",
                                response.status_code,
                                response.text,
                            )
                            break
                        response.raise_for_status()
                        LOGGER.info(
                            "전송 완료: HR=%d BPM, RR=%s",
                            sample.heart_rate_bpm,
                            sample.rr_intervals_ms,
                        )
                        break
                    except (httpx.RequestError, httpx.HTTPStatusError) as exc:
                        LOGGER.warning("서버 전송 실패, %.0f초 후 재시도: %s", retry_delay, exc)
                        await asyncio.sleep(retry_delay)
                        retry_delay = min(retry_delay * 2, 10.0)
            finally:
                queue.task_done()


async def run_gateway(
    *,
    server_url: str,
    session_id: str,
    device_selector: str | None,
    scan_timeout: float,
    reconnect_delay: float,
) -> None:
    try:
        from bleak import BleakClient, BleakScanner
    except ImportError as exc:  # pragma: no cover - depends on local installation
        raise RuntimeError("gateway 의존성이 없습니다: pip install -e '.[gateway]'") from exc

    endpoint = f"{server_url.rstrip('/')}/api/v1/sensors/polar-h10"
    queue: asyncio.Queue[GatewaySample] = asyncio.Queue(maxsize=256)
    sender = asyncio.create_task(_send_samples(queue, endpoint))

    try:
        while True:
            LOGGER.info("Polar H10을 검색하고 있습니다...")

            def matches(device: Any, advertisement: Any) -> bool:
                name = (advertisement.local_name or device.name or "").lower()
                address = str(device.address).lower()
                if POLAR_NAME not in name:
                    return False
                if device_selector is None:
                    return True
                selector = device_selector.lower()
                return selector in name or selector in address

            device = await BleakScanner.find_device_by_filter(matches, timeout=scan_timeout)
            if device is None:
                LOGGER.warning("Polar H10을 찾지 못했습니다. %.0f초 후 재검색합니다.", reconnect_delay)
                await asyncio.sleep(reconnect_delay)
                continue

            device_id = f"{device.name or 'Polar H10'} ({device.address})"
            disconnected = asyncio.Event()
            loop = asyncio.get_running_loop()

            def on_disconnect(_: Any) -> None:
                loop.call_soon_threadsafe(disconnected.set)

            try:
                async with BleakClient(device, disconnected_callback=on_disconnect) as client:
                    LOGGER.info("연결됨: %s", device_id)

                    def on_measurement(_: Any, raw: bytearray) -> None:
                        try:
                            measurement = parse_heart_rate_measurement(raw)
                        except ValueError as exc:
                            LOGGER.warning("잘못된 심박 패킷: %s", exc)
                            return
                        _put_latest(
                            queue,
                            GatewaySample(
                                session_id=session_id,
                                device_id=device_id,
                                captured_at=datetime.now(timezone.utc),
                                heart_rate_bpm=measurement.heart_rate_bpm,
                                rr_intervals_ms=measurement.rr_intervals_ms,
                            ),
                        )

                    await client.start_notify(HEART_RATE_MEASUREMENT_UUID, on_measurement)
                    LOGGER.info("심박 데이터 수신 시작 (Ctrl+C로 종료)")
                    await disconnected.wait()
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                LOGGER.warning("BLE 연결 오류: %s", exc)

            LOGGER.info("연결이 끊겼습니다. %.0f초 후 재연결합니다.", reconnect_delay)
            await asyncio.sleep(reconnect_delay)
    finally:
        sender.cancel()
        with suppress(asyncio.CancelledError):
            await sender


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Polar H10 심박/RR 데이터를 Artemis FastAPI 서버로 전송합니다."
    )
    parser.add_argument("--session-id", required=True, help="Unity와 공유할 session_id")
    parser.add_argument("--server-url", default="http://127.0.0.1:8000")
    parser.add_argument(
        "--device",
        help="장치 이름 또는 주소 일부. 생략하면 처음 발견한 Polar H10을 사용합니다.",
    )
    parser.add_argument("--scan-timeout", type=float, default=10.0)
    parser.add_argument("--reconnect-delay", type=float, default=3.0)
    parser.add_argument("--log-level", default="INFO", choices=("DEBUG", "INFO", "WARNING", "ERROR"))
    return parser


def main() -> None:
    args = build_parser().parse_args()
    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    try:
        asyncio.run(
            run_gateway(
                server_url=args.server_url,
                session_id=args.session_id,
                device_selector=args.device,
                scan_timeout=args.scan_timeout,
                reconnect_delay=args.reconnect_delay,
            )
        )
    except KeyboardInterrupt:
        LOGGER.info("Polar H10 gateway를 종료합니다.")
    except RuntimeError as exc:
        raise SystemExit(str(exc)) from exc


if __name__ == "__main__":
    main()

