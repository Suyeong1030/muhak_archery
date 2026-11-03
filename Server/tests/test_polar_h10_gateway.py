import pytest

from gateways.polar_h10 import parse_heart_rate_measurement


def test_parse_8_bit_heart_rate_without_rr() -> None:
    measurement = parse_heart_rate_measurement(bytes([0x00, 72]))
    assert measurement.heart_rate_bpm == 72
    assert measurement.rr_intervals_ms == []


def test_parse_heart_rate_and_multiple_rr_intervals() -> None:
    # Flags: RR present. 1024/1024 sec and 768/1024 sec.
    measurement = parse_heart_rate_measurement(
        bytes([0x10, 80, 0x00, 0x04, 0x00, 0x03])
    )
    assert measurement.heart_rate_bpm == 80
    assert measurement.rr_intervals_ms == [1000.0, 750.0]


def test_parse_16_bit_heart_rate_with_energy_and_rr() -> None:
    # Flags: 16-bit HR, energy expended, and RR present.
    measurement = parse_heart_rate_measurement(
        bytes([0x19, 0x2C, 0x01, 0x34, 0x12, 0x00, 0x04])
    )
    assert measurement.heart_rate_bpm == 300
    assert measurement.rr_intervals_ms == [1000.0]


def test_reject_incomplete_rr_interval() -> None:
    with pytest.raises(ValueError, match="incomplete RR"):
        parse_heart_rate_measurement(bytes([0x10, 80, 0x00]))

