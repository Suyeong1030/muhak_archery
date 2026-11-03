from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from .models import SessionReport, TrialReport
from .renderer import ReportRenderer
from .theme import ArtemisTheme, pillow_palette


class PillowReportRenderer(ReportRenderer):
    """Fixed-size report renderer intended for Unity textures."""

    _POSE_CONNECTIONS = (
        (0, 11), (0, 12), (11, 12),
        (11, 13), (13, 15), (12, 14), (14, 16),
        (11, 23), (12, 24), (23, 24),
        (23, 25), (25, 27), (27, 29), (29, 31),
        (24, 26), (26, 28), (28, 30), (30, 32),
    )

    def _font(self, path: Path, size: int) -> ImageFont.FreeTypeFont:
        return ImageFont.truetype(str(path), size=size)

    def _fonts(self, theme: ArtemisTheme) -> dict[str, ImageFont.FreeTypeFont]:
        typography = theme.typography
        return {
            "display": self._font(typography.title_font, typography.display_size),
            "page": self._font(typography.title_font, typography.page_title_size),
            "section": self._font(typography.body_bold_font, typography.section_title_size),
            "card": self._font(typography.body_bold_font, typography.card_title_size),
            "body": self._font(typography.body_font, typography.body_size),
            "body_medium": self._font(typography.body_medium_font, typography.body_size),
            "small": self._font(typography.body_font, typography.body_small_size),
            "caption": self._font(typography.body_medium_font, typography.caption_size),
            "metric_label": self._font(
                typography.body_medium_font,
                typography.metric_label_size,
            ),
            "metric": self._font(typography.body_medium_font, typography.metric_size),
            "score": self._font(typography.title_font, typography.overall_score_size),
        }

    def _canvas(self, theme: ArtemisTheme) -> tuple[Image.Image, ImageDraw.ImageDraw]:
        geometry = theme.geometry
        palette = pillow_palette(theme)
        image = Image.new(
            "RGBA",
            (geometry.canvas_width, geometry.canvas_height),
            palette["background"],
        )
        draw = ImageDraw.Draw(image, "RGBA")
        return image, draw

    def _panel(self, draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], theme: ArtemisTheme) -> None:
        palette = pillow_palette(theme)
        draw.rounded_rectangle(
            box,
            radius=theme.geometry.panel_radius,
            fill=palette["panel"],
            outline=palette["line_emphasis"],
            width=theme.geometry.border_width,
        )

    def _header(self, draw: ImageDraw.ImageDraw, title: str, fonts, theme: ArtemisTheme) -> None:
        s, c, g = theme.spacing, theme.colors, theme.geometry
        title_box = draw.textbbox((0, 0), title, font=fonts["page"])
        title_width = title_box[2] - title_box[0]
        title_height = title_box[3] - title_box[1]
        box = (
            s.page_horizontal,
            s.page_vertical,
            s.page_horizontal + title_width + s.xl * 2,
            s.page_vertical + title_height + s.lg * 2,
        )
        draw.rounded_rectangle(
            box,
            radius=g.panel_radius,
            fill=c.panel_highlight,
            outline=c.gold_dark,
            width=g.accent_border_width,
        )
        draw.text(
            (
                box[0] + s.xl,
                box[1] + s.lg - title_box[1],
            ),
            title,
            font=fonts["page"],
            fill=c.gold_bright,
        )

    def _metric_card(self, draw, box, label: str, value: str, fonts, theme: ArtemisTheme, accent: str | None = None) -> None:
        self._panel(draw, box, theme)
        x1, y1, _, _ = box
        pad = theme.spacing.card_padding
        draw.text(
            (x1 + pad, y1 + pad),
            label,
            font=fonts["metric_label"],
            fill=theme.colors.text_secondary,
        )
        draw.text(
            (x1 + pad, y1 + pad + theme.spacing.xl),
            value,
            font=fonts["metric"],
            fill=accent or theme.colors.text_primary,
        )

    async def render_summary(self, report: SessionReport, destination: Path, theme: ArtemisTheme) -> None:
        image, draw = self._canvas(theme)
        fonts, s, g, c = self._fonts(theme), theme.spacing, theme.geometry, theme.colors
        self._header(draw, "SESSION REPORT", fonts, theme)

        top = s.page_vertical + s.xxxl + s.xl
        score_width = 390
        gap = s.card_gap
        left = (s.page_horizontal, top, g.canvas_width - s.page_horizontal - score_width - gap, top + 310)
        right = (left[2] + gap, top, g.canvas_width - s.page_horizontal, top + 310)
        self._panel(draw, left, theme)
        self._panel(draw, right, theme)

        draw.text((left[0] + s.panel_padding, left[1] + s.panel_padding), "COACH'S NOTE", font=fonts["card"], fill=c.gold)
        lines = self._wrap(draw, report.message, fonts["body"], left[2] - left[0] - s.panel_padding * 2)
        draw.multiline_text(
            (left[0] + s.panel_padding, left[1] + s.panel_padding + s.xl),
            "\n".join(lines[:5]), font=fonts["body"], fill=c.text_primary,
            spacing=round(theme.typography.body_size * (theme.typography.body_line_height - 1)),
        )

        draw.text((right[0] + s.panel_padding, right[1] + s.panel_padding), "TOTAL SCORE", font=fonts["metric_label"], fill=c.text_secondary)
        draw.text((right[0] + s.panel_padding, right[1] + s.xxxl), str(report.total_score), font=fonts["score"], fill=c.gold_bright)
        draw.text((right[0] + s.panel_padding + 180, right[1] + 145), "/ 30", font=fonts["section"], fill=c.text_secondary)
        draw.text((right[0] + s.panel_padding, right[3] - s.xl), f"평균 {report.average_score:.1f}점", font=fonts["body_medium"], fill=c.text_secondary)

        card_top = top + 310 + gap
        card_width = (g.canvas_width - s.page_horizontal * 2 - gap * 2) // 3
        scored = [trial for trial in report.trials if trial.score is not None]
        best = max(scored, key=lambda item: item.score or 0) if scored else None
        max_hr = max((trial.max_heart_rate_bpm or 0 for trial in report.trials), default=0)
        stability = sum(trial.min_stability or 0 for trial in report.trials) / max(len(report.trials), 1)
        values = [
            ("BEST TRIAL", f"#{best.trial_number}  ·  {best.score}점" if best else "—"),
            ("MAX HEART RATE", f"{max_hr} BPM" if max_hr else "측정 안 함"),
            ("AVG STABILITY", f"{stability:.2f}"),
        ]
        for index, (label, value) in enumerate(values):
            x = s.page_horizontal + index * (card_width + gap)
            self._metric_card(draw, (x, card_top, x + card_width, card_top + 155), label, value, fonts, theme, c.gold if index == 0 else None)

        destination.parent.mkdir(parents=True, exist_ok=True)
        image.convert("RGB").save(destination, "PNG")

    async def render_sensor_timeline(self, report: SessionReport, destination: Path, theme: ArtemisTheme) -> None:
        image, draw = self._canvas(theme)
        fonts, s, g, c = self._fonts(theme), theme.spacing, theme.geometry, theme.colors
        self._header(draw, "SENSOR TIMELINE", fonts, theme)
        top = s.page_vertical + s.xxxl + s.xl
        box = (s.page_horizontal, top, g.canvas_width - s.page_horizontal, g.canvas_height - s.page_vertical)
        self._panel(draw, box, theme)

        heart: list[float | None] = []
        rr: list[float | None] = []
        trial_starts: list[tuple[int, int]] = []

        for trial in report.trials:
            if heart:
                heart.append(None)
                rr.append(None)
            trial_starts.append((trial.trial_number, len(heart)))
            for sample in trial.polar_samples:
                heart.append(float(sample.heart_rate_bpm))
                rr.append(
                    sum(sample.rr_intervals_ms) / len(sample.rr_intervals_ms)
                    if sample.rr_intervals_ms
                    else None
                )

        chart_left = box[0] + s.xxl
        chart_right = box[2] - s.xl
        chart_top = box[1] + 105
        chart_bottom = box[3] - s.xl
        chart_gap = 75
        chart_height = (chart_bottom - chart_top - chart_gap) / 2
        heart_plot = (chart_left, chart_top, chart_right, chart_top + chart_height)
        rr_plot = (chart_left, chart_top + chart_height + chart_gap, chart_right, chart_bottom)

        for plot in (heart_plot, rr_plot):
            for index in range(5):
                y = plot[1] + index * (plot[3] - plot[1]) / 4
                draw.line((plot[0], y, plot[2], y), fill=c.grid, width=max(1, round(g.grid_line_width)))

        heart_present = [value for value in heart if value is not None]
        rr_present = [value for value in rr if value is not None]
        heart_min = min(heart_present, default=60) - 5
        heart_max = max(heart_present, default=120) + 5
        rr_min = min(rr_present, default=400) - 25
        rr_max = max(rr_present, default=1200) + 25

        self._series(draw, heart_plot, heart, heart_min, max(heart_max, heart_min + 1), c.heart_rate, g.chart_line_width)
        self._series(draw, rr_plot, rr, rr_min, max(rr_max, rr_min + 1), c.gold, g.chart_line_width)

        draw.text((heart_plot[0], heart_plot[1] - s.xl), "HEART RATE (BPM)", font=fonts["caption"], fill=c.heart_rate)
        draw.text((rr_plot[0], rr_plot[1] - s.xl), "RR INTERVAL (MS)", font=fonts["caption"], fill=c.gold)
        draw.text((heart_plot[2] - 100, heart_plot[1] - s.xl), f"{heart_min:.0f}-{heart_max:.0f}", font=fonts["caption"], fill=c.text_muted)
        draw.text((rr_plot[2] - 120, rr_plot[1] - s.xl), f"{rr_min:.0f}-{rr_max:.0f}", font=fonts["caption"], fill=c.text_muted)

        for trial_number, index in trial_starts:
            x = chart_left + index / max(len(heart) - 1, 1) * (chart_right - chart_left)
            for plot in (heart_plot, rr_plot):
                draw.line((x, plot[1], x, plot[3]), fill=c.gold_dark, width=max(1, round(g.chart_reference_line_width)))
            draw.text((x + s.xs, heart_plot[1] + s.xs), f"TRIAL {trial_number}", font=fonts["caption"], fill=c.text_secondary)

        if not heart_present:
            draw.text((chart_left, heart_plot[1] + s.xl), "NO POLAR H10 DATA COLLECTED", font=fonts["body"], fill=c.text_muted)
        image.convert("RGB").save(destination, "PNG")

    async def render_trial(self, report: SessionReport, trial: TrialReport, destination: Path, theme: ArtemisTheme) -> None:
        image, draw = self._canvas(theme)
        fonts, s, g, c = self._fonts(theme), theme.spacing, theme.geometry, theme.colors
        self._header(draw, f"TRIAL {trial.trial_number}", fonts, theme)
        top = s.page_vertical + s.xxxl + s.xl
        left = (s.page_horizontal, top, 1040, g.canvas_height - s.page_vertical)
        right = (left[2] + s.card_gap, top, g.canvas_width - s.page_horizontal, g.canvas_height - s.page_vertical)
        self._panel(draw, left, theme)
        self._panel(draw, right, theme)

        draw.text((left[0] + s.panel_padding, left[1] + s.panel_padding), "RELEASE METRICS", font=fonts["card"], fill=c.gold)
        pose = trial.release_pose
        metrics = [
            ("드로우 팔꿈치", self._value(pose.draw_elbow_angle_deg if pose else None, "°")),
            ("어깨 기울기", self._value(pose.shoulder_tilt_deg if pose else None, "°")),
            ("상체 기울기", self._value(pose.torso_tilt_deg if pose else None, "°")),
            ("앵커 거리", self._value(pose.anchor_distance if pose else None)),
            ("안정성", self._value(trial.min_stability)),
            ("최대 심박", self._value(trial.max_heart_rate_bpm, " BPM")),
        ]
        metric_top = left[1] + s.xxxl + s.lg
        cell_width = (left[2] - left[0] - s.panel_padding * 2 - s.card_gap) // 2
        for index, (label, value) in enumerate(metrics):
            column, row = index % 2, index // 2
            x = left[0] + s.panel_padding + column * (cell_width + s.card_gap)
            y = metric_top + row * 100
            draw.text((x, y), label, font=fonts["metric_label"], fill=c.text_secondary)
            draw.text((x, y + s.xl), value, font=fonts["section"], fill=c.text_primary)

        note_y = left[3] - 205
        draw.rounded_rectangle((left[0] + s.panel_padding, note_y, left[2] - s.panel_padding, left[3] - s.panel_padding), radius=g.card_radius, fill=c.panel_highlight)
        note_text = trial.feedback or "측정된 피드백이 없습니다."
        note_lines = self._wrap(
            draw,
            note_text,
            fonts["body"],
            left[2] - left[0] - s.panel_padding * 2 - s.md * 2,
        )
        draw.multiline_text(
            (left[0] + s.panel_padding + s.md, note_y + s.md),
            "\n".join(note_lines[:4]),
            font=fonts["body"],
            fill=c.text_primary,
            spacing=s.xs,
        )

        draw.text((right[0] + s.panel_padding, right[1] + s.panel_padding), "SCORE", font=fonts["metric_label"], fill=c.text_secondary)
        draw.text((right[0] + s.panel_padding, right[1] + s.xxxl), str(trial.score) if trial.score is not None else "—", font=fonts["score"], fill=c.gold_bright)

        pose_top = right[1] + 215
        draw.line(
            (right[0] + s.panel_padding, pose_top, right[2] - s.panel_padding, pose_top),
            fill=c.line,
            width=g.border_width,
        )
        draw.text((right[0] + s.panel_padding, pose_top + s.lg), "POSE OVERLAY", font=fonts["card"], fill=c.text_secondary)
        legend_y = pose_top + s.lg
        legend_x = right[2] - 205
        draw.ellipse((legend_x, legend_y, legend_x + s.sm, legend_y + s.sm), fill=c.posture)
        draw.text((legend_x + s.md, legend_y - 2), "YOU", font=fonts["caption"], fill=c.text_muted)
        draw.ellipse((legend_x + 85, legend_y, legend_x + 85 + s.sm, legend_y + s.sm), fill=c.gold)
        draw.text((legend_x + 85 + s.md, legend_y - 2), "REF", font=fonts["caption"], fill=c.text_muted)
        pose_box = (
            right[0] + s.panel_padding,
            pose_top + s.xxl,
            right[2] - s.panel_padding,
            right[3] - s.panel_padding,
        )
        self._pose_overlay(draw, pose_box, trial, theme)
        image.convert("RGB").save(destination, "PNG")

    @staticmethod
    def _wrap(draw, text: str, font, width: int) -> list[str]:
        lines, current = [], ""
        for word in text.split():
            candidate = f"{current} {word}".strip()
            if draw.textlength(candidate, font=font) <= width:
                current = candidate
            else:
                if current:
                    lines.append(current)
                current = word
        if current:
            lines.append(current)
        return lines

    @staticmethod
    def _series(draw, plot, values, minimum, maximum, color, width) -> None:
        points = []
        for index, value in enumerate(values):
            if value is None:
                if len(points) > 1:
                    draw.line(points, fill=color, width=round(width), joint="curve")
                points = []
                continue
            x = plot[0] + index / max(len(values) - 1, 1) * (plot[2] - plot[0])
            ratio = min(1.0, max(0.0, (float(value) - minimum) / (maximum - minimum)))
            y = plot[3] - ratio * (plot[3] - plot[1])
            points.append((x, y))
        if len(points) > 1:
            draw.line(points, fill=color, width=round(width), joint="curve")

    @staticmethod
    def _value(value, suffix: str = "") -> str:
        if value is None:
            return "측정 안 함"
        if isinstance(value, float):
            return f"{value:.2f}{suffix}"
        return f"{value}{suffix}"

    @staticmethod
    def _target(draw, center, radius: int, trial: TrialReport, theme: ArtemisTheme) -> None:
        c, g = theme.colors, theme.geometry
        rings = [c.silver_bright, c.background_deep, c.info, c.danger, c.gold]
        for index, color in enumerate(rings):
            r = radius - index * radius / len(rings)
            draw.ellipse((center[0] - r, center[1] - r, center[0] + r, center[1] + r), fill=color, outline=c.line, width=g.border_width)
        if trial.impact_point:
            x = center[0] + trial.impact_point.x * radius
            y = center[1] - trial.impact_point.y * radius
            marker = theme.spacing.sm
            draw.ellipse((x - marker, y - marker, x + marker, y + marker), fill=c.silver_bright, outline=c.background_deep, width=g.accent_border_width)
        else:
            label = "IMPACT DATA PENDING"
            font = ImageFont.truetype(str(theme.typography.body_medium_font), theme.typography.caption_size)
            bounds = draw.textbbox((0, 0), label, font=font)
            draw.text((center[0] - (bounds[2] - bounds[0]) / 2, center[1] + radius + theme.spacing.md), label, font=font, fill=c.text_muted)

    @classmethod
    def _pose_overlay(cls, draw, box, trial: TrialReport, theme: ArtemisTheme) -> None:
        actual = trial.release_landmarks
        reference = trial.reference_landmarks
        if not actual or not reference:
            font = ImageFont.truetype(
                str(theme.typography.body_medium_font),
                theme.typography.body_small_size,
            )
            draw.text(
                (box[0], box[1] + theme.spacing.xl),
                "POSE SNAPSHOT NOT AVAILABLE",
                font=font,
                fill=theme.colors.text_muted,
            )
            return

        actual_points = cls._normalized_pose_points(actual, box)
        reference_points = cls._normalized_pose_points(reference, box)

        # MediaPipe 입력 또는 레퍼런스 이미지가 서로 반대 방향으로
        # 촬영됐다면 좌/우 어깨 벡터의 X 방향이 반대가 됩니다. 이 경우
        # REF만 수평 반전해 사용자의 화면 방향과 맞춘 뒤 오버레이합니다.
        actual_shoulder_direction = actual[11].x - actual[12].x
        reference_shoulder_direction = reference[11].x - reference[12].x

        if actual_shoulder_direction * reference_shoulder_direction < 0:
            alignment_x = (box[0] + box[2]) / 2
            reference_points = [
                (2 * alignment_x - x, y)
                for x, y in reference_points
            ]

        cls._draw_skeleton(
            draw,
            reference_points,
            theme.colors.gold,
            theme.geometry.chart_reference_line_width,
            theme,
        )
        cls._draw_skeleton(
            draw,
            actual_points,
            theme.colors.posture,
            theme.geometry.chart_line_width,
            theme,
        )

    @classmethod
    def _draw_skeleton(cls, draw, points, color, line_width, theme) -> None:
        width = max(1, round(line_width))
        for start, end in cls._POSE_CONNECTIONS:
            draw.line((points[start], points[end]), fill=color, width=width)
        radius = max(2, theme.spacing.xs // 2)
        for index in {point for connection in cls._POSE_CONNECTIONS for point in connection}:
            x, y = points[index]
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)

    @staticmethod
    def _normalized_pose_points(landmarks, box):
        """Align by shoulder midpoint and shoulder width before overlaying."""
        left_shoulder = landmarks[11]
        right_shoulder = landmarks[12]
        center_x = (left_shoulder.x + right_shoulder.x) / 2
        center_y = (left_shoulder.y + right_shoulder.y) / 2
        shoulder_width = math.hypot(
            left_shoulder.x - right_shoulder.x,
            left_shoulder.y - right_shoulder.y,
        ) or 1.0

        box_width = box[2] - box[0]
        box_height = box[3] - box[1]
        scale = min(
            box_width * 0.42 / shoulder_width,
            box_height * 0.21 / shoulder_width,
        )
        target_x = (box[0] + box[2]) / 2
        target_y = box[1] + box_height * 0.18
        return [
            (
                target_x + (point.x - center_x) * scale,
                target_y + (point.y - center_y) * scale,
            )
            for point in landmarks
        ]
