from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Final


# ============================================================
# Paths
# ============================================================

BASE_DIR: Final[Path] = Path(__file__).resolve().parent
ASSETS_DIR: Final[Path] = BASE_DIR / "assets"
FONTS_DIR: Final[Path] = ASSETS_DIR / "fonts"
IMAGES_DIR: Final[Path] = ASSETS_DIR / "images"

# 실제 프로젝트에 넣은 폰트 파일명으로 수정
TITLE_FONT_PATH: Final[Path] = FONTS_DIR / "Cinzel-SemiBold.ttf"
BODY_FONT_PATH: Final[Path] = FONTS_DIR / "Pretendard-Regular.ttf"
BODY_MEDIUM_FONT_PATH: Final[Path] = FONTS_DIR / "Pretendard-Medium.ttf"
BODY_BOLD_FONT_PATH: Final[Path] = FONTS_DIR / "Pretendard-SemiBold.ttf"


# ============================================================
# Color Utilities
# ============================================================

def hex_to_rgb(hex_color: str) -> tuple[int, int, int]:
    """Convert '#RRGGBB' to an RGB tuple."""
    value = hex_color.strip().lstrip("#")

    if len(value) != 6:
        raise ValueError(f"Expected 6-digit hex color, got: {hex_color}")

    return tuple(
        int(value[index:index + 2], 16)
        for index in (0, 2, 4)
    )


def hex_to_rgba(
    hex_color: str,
    alpha: int = 255,
) -> tuple[int, int, int, int]:
    """Convert '#RRGGBB' to an RGBA tuple."""
    if not 0 <= alpha <= 255:
        raise ValueError("alpha must be between 0 and 255")

    return (*hex_to_rgb(hex_color), alpha)


def rgba_css(hex_color: str, alpha: float) -> str:
    """Convert a hex color and 0–1 alpha to a CSS rgba string."""
    if not 0.0 <= alpha <= 1.0:
        raise ValueError("alpha must be between 0.0 and 1.0")

    red, green, blue = hex_to_rgb(hex_color)
    return f"rgba({red}, {green}, {blue}, {alpha})"


# ============================================================
# Theme Tokens
# ============================================================

@dataclass(frozen=True)
class ArtemisColors:
    background_deep: str = "#030A12"
    background: str = "#071521"
    background_elevated: str = "#0A1B29"

    panel: str = "#0B1C2B"
    panel_soft: str = "#102534"
    panel_highlight: str = "#152D3D"

    text_primary: str = "#E9EDF0"
    text_secondary: str = "#9EABB5"
    text_muted: str = "#64717B"
    text_disabled: str = "#46525C"

    silver: str = "#C9D3D9"
    silver_bright: str = "#EEF3F5"
    gold: str = "#D8C09A"
    gold_bright: str = "#E7D2AD"
    gold_dark: str = "#9C8562"

    line: str = "#2A3946"
    line_soft: str = "#1C2B37"
    grid: str = "#263744"

    success: str = "#A9CBBF"
    warning: str = "#D5B581"
    danger: str = "#C98E8E"
    info: str = "#8FAFC4"

    breathing: str = "#D8C09A"
    heart_rate: str = "#C98E8E"
    posture: str = "#8FAFC4"
    stability: str = "#A9CBBF"
    reference: str = "#64717B"


@dataclass(frozen=True)
class ArtemisTypography:
    title_font: Path = TITLE_FONT_PATH
    body_font: Path = BODY_FONT_PATH
    body_medium_font: Path = BODY_MEDIUM_FONT_PATH
    body_bold_font: Path = BODY_BOLD_FONT_PATH

    display_size: int = 76
    page_title_size: int = 60
    section_title_size: int = 30
    card_title_size: int = 28
    body_size: int = 24
    body_small_size: int = 20
    caption_size: int = 18
    metric_label_size: int = 28
    metric_size: int = 42
    overall_score_size: int = 92

    body_line_height: float = 1.55
    title_letter_spacing_px: float = 7.0
    section_letter_spacing_px: float = 4.0
    caption_letter_spacing_px: float = 2.0


@dataclass(frozen=True)
class ArtemisSpacing:
    xs: int = 8
    sm: int = 12
    md: int = 20
    lg: int = 32
    xl: int = 48
    xxl: int = 64
    xxxl: int = 88

    page_horizontal: int = 72
    page_vertical: int = 56
    panel_padding: int = 42
    card_padding: int = 24
    section_gap: int = 40
    card_gap: int = 18


@dataclass(frozen=True)
class ArtemisGeometry:
    canvas_width: int = 1600
    canvas_height: int = 900

    panel_radius: int = 12
    card_radius: int = 8
    button_radius: int = 4

    border_width: int = 1
    accent_border_width: int = 2

    chart_line_width: float = 2.2
    chart_reference_line_width: float = 1.0
    grid_line_width: float = 0.8


@dataclass(frozen=True)
class ArtemisOpacity:
    panel: float = 0.92
    panel_soft: float = 0.76
    panel_glass: float = 0.62

    border: float = 0.22
    border_emphasis: float = 0.55
    grid: float = 0.30
    overlay: float = 0.58
    muted: float = 0.70


@dataclass(frozen=True)
class ArtemisChartStyle:
    figure_dpi: int = 180
    export_dpi: int = 220

    chart_height_inches: float = 3.2
    chart_width_inches: float = 8.0

    show_top_spine: bool = False
    show_right_spine: bool = False
    show_left_spine: bool = True
    show_bottom_spine: bool = True

    grid_axis: str = "y"
    grid_linestyle: str = "-"
    tick_label_size: int = 10
    axis_label_size: int = 11
    title_size: int = 14

    transparent_export: bool = True
    tight_layout: bool = True


@dataclass(frozen=True)
class ArtemisTheme:
    colors: ArtemisColors = ArtemisColors()
    typography: ArtemisTypography = ArtemisTypography()
    spacing: ArtemisSpacing = ArtemisSpacing()
    geometry: ArtemisGeometry = ArtemisGeometry()
    opacity: ArtemisOpacity = ArtemisOpacity()
    charts: ArtemisChartStyle = ArtemisChartStyle()


THEME: Final[ArtemisTheme] = ArtemisTheme()


# ============================================================
# CSS Tokens
# ============================================================

def build_css_variables(theme: ArtemisTheme = THEME) -> str:
    """Generate CSS variables for HTML/Jinja report templates."""
    colors = theme.colors
    spacing = theme.spacing
    geometry = theme.geometry
    opacity = theme.opacity
    typography = theme.typography

    return f"""
:root {{
    --artemis-bg-deep: {colors.background_deep};
    --artemis-bg: {colors.background};
    --artemis-bg-elevated: {colors.background_elevated};

    --artemis-panel: {colors.panel};
    --artemis-panel-soft: {colors.panel_soft};
    --artemis-panel-highlight: {colors.panel_highlight};

    --artemis-text-primary: {colors.text_primary};
    --artemis-text-secondary: {colors.text_secondary};
    --artemis-text-muted: {colors.text_muted};
    --artemis-text-disabled: {colors.text_disabled};

    --artemis-silver: {colors.silver};
    --artemis-silver-bright: {colors.silver_bright};
    --artemis-gold: {colors.gold};
    --artemis-gold-bright: {colors.gold_bright};
    --artemis-gold-dark: {colors.gold_dark};

    --artemis-line: {colors.line};
    --artemis-line-soft: {colors.line_soft};
    --artemis-grid: {colors.grid};

    --artemis-success: {colors.success};
    --artemis-warning: {colors.warning};
    --artemis-danger: {colors.danger};
    --artemis-info: {colors.info};

    --artemis-panel-alpha: {opacity.panel};
    --artemis-panel-soft-alpha: {opacity.panel_soft};
    --artemis-panel-glass-alpha: {opacity.panel_glass};
    --artemis-border-alpha: {opacity.border};
    --artemis-border-emphasis-alpha: {opacity.border_emphasis};

    --space-xs: {spacing.xs}px;
    --space-sm: {spacing.sm}px;
    --space-md: {spacing.md}px;
    --space-lg: {spacing.lg}px;
    --space-xl: {spacing.xl}px;
    --space-xxl: {spacing.xxl}px;

    --page-horizontal: {spacing.page_horizontal}px;
    --page-vertical: {spacing.page_vertical}px;
    --panel-padding: {spacing.panel_padding}px;
    --card-padding: {spacing.card_padding}px;

    --panel-radius: {geometry.panel_radius}px;
    --card-radius: {geometry.card_radius}px;
    --border-width: {geometry.border_width}px;

    --display-size: {typography.display_size}px;
    --page-title-size: {typography.page_title_size}px;
    --section-title-size: {typography.section_title_size}px;
    --body-size: {typography.body_size}px;
    --caption-size: {typography.caption_size}px;
}}
""".strip()


# ============================================================
# Matplotlib Integration
# ============================================================

def matplotlib_rc_params(
    theme: ArtemisTheme = THEME,
) -> dict[str, object]:
    """Shared matplotlib rcParams for ARTEMIS charts."""
    colors = theme.colors
    typography = theme.typography
    charts = theme.charts
    geometry = theme.geometry
    opacity = theme.opacity

    font_family = "sans-serif"

    if typography.body_font.exists():
        font_family = typography.body_font.stem

    return {
        "figure.facecolor": colors.background,
        "axes.facecolor": colors.panel,
        "savefig.facecolor": colors.background,
        "text.color": colors.text_primary,
        "axes.labelcolor": colors.text_secondary,
        "axes.titlecolor": colors.text_primary,
        "xtick.color": colors.text_muted,
        "ytick.color": colors.text_muted,
        "axes.edgecolor": colors.line,
        "axes.linewidth": geometry.border_width,
        "grid.color": colors.grid,
        "grid.alpha": opacity.grid,
        "grid.linewidth": geometry.grid_line_width,
        "grid.linestyle": charts.grid_linestyle,
        "font.family": font_family,
        "font.size": typography.body_small_size,
        "axes.titlesize": charts.title_size,
        "axes.labelsize": charts.axis_label_size,
        "xtick.labelsize": charts.tick_label_size,
        "ytick.labelsize": charts.tick_label_size,
        "lines.linewidth": geometry.chart_line_width,
        "lines.solid_capstyle": "round",
        "legend.facecolor": colors.panel_soft,
        "legend.edgecolor": colors.line,
        "legend.labelcolor": colors.text_secondary,
        "legend.framealpha": opacity.panel_soft,
        "figure.dpi": charts.figure_dpi,
        "savefig.dpi": charts.export_dpi,
        "savefig.transparent": charts.transparent_export,
        "savefig.bbox": "tight",
        "savefig.pad_inches": 0.12,
    }


def register_matplotlib_fonts(
    theme: ArtemisTheme = THEME,
) -> None:
    """Register bundled fonts; missing files are silently skipped."""
    from matplotlib import font_manager

    font_paths = [
        theme.typography.title_font,
        theme.typography.body_font,
        theme.typography.body_medium_font,
        theme.typography.body_bold_font,
    ]

    for font_path in font_paths:
        if font_path.exists():
            font_manager.fontManager.addfont(str(font_path))


def apply_artemis_axes_style(
    ax,
    *,
    title: str | None = None,
    xlabel: str | None = None,
    ylabel: str | None = None,
    show_grid: bool = True,
    theme: ArtemisTheme = THEME,
) -> None:
    """Apply ARTEMIS styling to a matplotlib Axes object."""
    colors = theme.colors
    charts = theme.charts
    geometry = theme.geometry
    opacity = theme.opacity

    ax.set_facecolor(colors.panel)
    ax.spines["top"].set_visible(charts.show_top_spine)
    ax.spines["right"].set_visible(charts.show_right_spine)
    ax.spines["left"].set_visible(charts.show_left_spine)
    ax.spines["bottom"].set_visible(charts.show_bottom_spine)

    for spine in ax.spines.values():
        spine.set_color(colors.line)
        spine.set_linewidth(geometry.border_width)

    ax.tick_params(axis="both", colors=colors.text_muted, length=0, pad=8)

    if show_grid:
        ax.grid(
            True,
            axis=charts.grid_axis,
            color=colors.grid,
            alpha=opacity.grid,
            linewidth=geometry.grid_line_width,
            linestyle=charts.grid_linestyle,
        )
    else:
        ax.grid(False)

    if title:
        ax.set_title(
            title.upper(),
            color=colors.text_primary,
            fontweight="medium",
            loc="left",
            pad=18,
        )

    if xlabel:
        ax.set_xlabel(xlabel, color=colors.text_secondary, labelpad=12)

    if ylabel:
        ax.set_ylabel(ylabel, color=colors.text_secondary, labelpad=12)


# ============================================================
# Pillow Helpers
# ============================================================

def pillow_palette(
    theme: ArtemisTheme = THEME,
) -> dict[str, tuple[int, int, int, int]]:
    """RGBA colors for Pillow-based image rendering."""
    colors = theme.colors
    opacity = theme.opacity

    return {
        "background": hex_to_rgba(colors.background_deep),
        "background_elevated": hex_to_rgba(colors.background_elevated),
        "panel": hex_to_rgba(colors.panel, round(255 * opacity.panel)),
        "panel_soft": hex_to_rgba(colors.panel_soft, round(255 * opacity.panel_soft)),
        "panel_glass": hex_to_rgba(colors.panel, round(255 * opacity.panel_glass)),
        "text_primary": hex_to_rgba(colors.text_primary),
        "text_secondary": hex_to_rgba(colors.text_secondary),
        "text_muted": hex_to_rgba(colors.text_muted),
        "silver": hex_to_rgba(colors.silver),
        "gold": hex_to_rgba(colors.gold),
        "line": hex_to_rgba(colors.line, round(255 * opacity.border)),
        "line_emphasis": hex_to_rgba(colors.gold, round(255 * opacity.border_emphasis)),
        "success": hex_to_rgba(colors.success),
        "warning": hex_to_rgba(colors.warning),
        "danger": hex_to_rgba(colors.danger),
        "info": hex_to_rgba(colors.info),
    }


# ============================================================
# Validation
# ============================================================

def validate_theme_assets(
    theme: ArtemisTheme = THEME,
) -> list[str]:
    """Return warnings for missing theme assets."""
    warnings: list[str] = []
    required_fonts = {
        "title font": theme.typography.title_font,
        "body font": theme.typography.body_font,
        "body medium font": theme.typography.body_medium_font,
        "body bold font": theme.typography.body_bold_font,
    }

    for label, path in required_fonts.items():
        if not path.exists():
            warnings.append(f"Missing {label}: {path}")

    return warnings
