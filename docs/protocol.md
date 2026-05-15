# Razer Audio Mixer Wire Protocol

USB ID: **1532:053e** (RZ19-0386)

Byte positions are 0-indexed throughout. Each section notes which capture file(s) it was derived from.

## Traffic paths

| Path | Transport | Direction | Purpose |
|---|---|---|---|
| Control (SET_REPORT) | EP0, bmRequestType=0x21, bRequest=9 | Host to Device | Settings, lighting, mute feedback |
| Interrupt-IN | EP 0x84 | Device to Host | Fader positions, button state (pushed unsolicited) |

Audio is plain USB Audio Class 2.0 on separate interfaces; `snd-usb-audio` handles it and it's not documented here.

Note on tshark: HID interrupt-IN data (EP 0x84) lands in the `usbhid.data` field, not `usb.capdata`. To extract it:

```bash
tshark -r FILE -Y "usb.endpoint_address == 0x84 && usb.urb_type == 0x43" \
       -T fields -e usbhid.data
```

---

## Control path report IDs

### Report 0x03 -- Mic preamp gain

Length: 7 bytes.

```
03  18  00  c0  10  <gain>  <gain>
 0   1   2   3   4     5      6
```

Bytes 5 and 6 both carry the same gain value. Range observed: `0x00` to `0x45` (69 steps; exact dB mapping not confirmed).

Report 0x03 also appears during mic mute with altered bytes 4-6, likely hard-silencing the preamp. Exact encoding for that case is not decoded yet.

Source: `docs/pcaps/mic-gain-min-to-max.pcapng` (41 distinct frames across the full slider range).

---

### Report 0x04 -- Parameter zone select

Length: 5 bytes.

```
04  5f  fc  00  <addr>
 0   1   2   3     4
```

Must be sent immediately before every Report 0x13 write to the same `<addr>`. The device ignores Report 0x13 writes that are not preceded by a matching zone select.

Known addresses: `0x30` (compressor/gate enable and compressor params), `0x34` (gate params), `0x54` `0x58` `0x5c` (flush).

---

### Report 0x0a -- 48V phantom power

Length: 2 bytes.

```
0a  <state>
 0     1
```

`0x05` = on, `0x02` = off.

Source: `phantom-on_synapse.pcapng`, `phantom-off_synapse.pcapng`.

---

### Report 0x13 -- DSP parameter write

Length: 9 bytes.

```
13  5f  fc  00  <addr>  <b5>  <val_hi>  <val_lo>  <type>
 0   1   2   3     4      5       6         7         8
```

Bytes 1-3 (`5f fc 00`) are constant across all observed frames.

#### Address 0x30 -- gate/compressor enable and compressor parameters

Enable bitmask (`b5`, `val_hi`, `val_lo` all zero):

```
13 5f fc 00  30  00  00  00  <mask>
```

Bit 0 = gate enabled, bit 1 = compressor enabled.

| mask | Gate | Compressor |
|------|------|------------|
| 0x00 | off  | off        |
| 0x01 | on   | off        |
| 0x02 | off  | on (inferred from bitmask, not directly captured) |
| 0x03 | on   | on         |

Source: `mic-gate_on.pcapng`, `mic-gate_off.pcapng`, `mic-comp_on.pcapng`, `mic-comp_off.pcapng`.

Compressor parameters all use `addr=0x30` and `b5=0x00`:

```
13 5f fc 00  30  00  <val_hi>  <val_lo>  <type>
```

| Parameter | type | Encoding | Observed range |
|---|---|---|---|
| Threshold | `0x13` | linear u8 in val_lo | 0x00 to 0x25 |
| Ratio | `0x33` | ratio x 100, big-endian u16 | 100 to 1000 (1:1 to 10:1) |
| Soft knee width | `0x53` | linear u8 in val_lo | 0x01 to 0x4c |
| Attack time | `0x73` | ms, big-endian u16 | 0x0014 to 0x0032 (20 to 50 ms) |
| Release time | `0x93` | ms, big-endian u16 | 0x004b to 0x00c8 (75 to 200 ms) |
| Makeup gain | `0xb3` | dB in val_lo | 0x00 to 0x16 (0 to 22 dB) |

```
# threshold = 7
13 5f fc 00 30 00 00 07 13

# ratio 5:1  (500 = 0x01f4)
13 5f fc 00 30 00 01 f4 33

# attack 40 ms (0x0028)
13 5f fc 00 30 00 00 28 73
```

Source: `mic-comp-threshold_min-to-max.pcapng`, `mic-comp-ratio_1-to-5-to-10.pcapng`, `mic-comp-attack-time_*.pcapng`, `mic-comp-release-time_*.pcapng`, `mic-comp-fain_*.pcapng`, `mic-comp-soft-knee-width_min-to-max.pcapng`.

---

#### Address 0x34 -- noise gate parameters

All gate params use `addr=0x34` and `b5=0x80`:

```
13 5f fc 00  34  80  <val_hi>  <val_lo>  <type>
```

| Parameter | type | Encoding | Observed range |
|---|---|---|---|
| Threshold | `0xb1` | big-endian u16 | 0x0001 to 0x0400 |
| Reduction | `0xc1` | big-endian u16 | 0x0001 to 0x0400 |
| Release time | `0xd1` | ms, big-endian u16 | 0x0028 to 0x0080 (40 to 128 ms) |
| Attack time | `0xe1` | ms, big-endian u16 | 0x0027 to 0x0080 (~39 to 128 ms) |

```
# gate threshold = max (0x0400)
13 5f fc 00 34 80 04 00 b1
```

Source: `mic-gate-threshold_min-to-max.pcapng`, `mic-gate-reduction_min-to-max.pcapng`, `mic-gate-attack-time_*.pcapng`, `mic-gate-release-time_*.pcapng`.

---

#### Addresses 0x54 / 0x58 / 0x5c -- flush

Sent at the end of every DSP settings block with all-zero val bytes:

```
13 5f fc 00 54 00 00 00 00
13 5f fc 00 58 00 00 00 00
13 5f fc 00 5c 00 00 00 00
```

Each is preceded by a matching Report 0x04. These signal end-of-block to the DSP. Skipping them appears to cause the device to discard the preceding parameter writes.

---

### Report 0x07 -- lighting (64-byte padded payload)

All lighting commands are 64 bytes with unused bytes zeroed. Four sub-commands, distinguished by bytes 7 and 8.

#### Sub-command A -- zone enable/brightness (byte7=`0x0f`, byte8=`0x04`)

```
07 00 1f 00 00 00 03  0f  04  01  <zone_id>  <brightness>  00...
 0  1  2  3  4  5  6   7   8   9      10          11
```

`brightness`: `0x00` = off, `0x64` = full brightness.

| zone_id | Zone |
|---------|------|
| `0x04`  | Wordmark |
| `0x05`  | Fader base (all 4 channels) |
| `0x06`  | Ch1 ring |
| `0x07`  | Ch2 ring |
| `0x08`  | Ch3 ring |
| `0x09`  | Ch4 ring |
| `0x0a`  | Channel number digits |
| `0x10`  | Channel mute rings (grouped) |
| `0x20`  | Mic-mute ring |
| `0x21`  | Bleep button |

Source: `lighting-*_on.pcapng` and `lighting-*_off.pcapng` captures.

---

#### Sub-command B -- static color (byte7=`0x0f`, byte8=`0x02`, effect=`0x01`)

```
07 00 1f 00 00 00  <b6>  0f  02  00  <addr>  01  00  00  <n>  [R G B x n]  00...
 0  1  2  3  4  5    6    7   8   9     10   11  12  13  14
```

`n` (byte 14) is the LED count for the zone. RGB triplets follow immediately after, one per LED.

`b6` is coupled to the LED count. Use the value observed in the capture for each zone; do not compute it.

| addr | n | Zone | Notes |
|------|---|------|-------|
| `0x04` | 1 | Wordmark | Always `ff ff ff` in captures |
| `0x05` | 4 | Fader base | color[i] = ch(i+1) base color |
| `0x06` | 1 | Ch1 ring | Shared by fader-foreground and channel-mute zones |
| `0x07` | 1 | Ch2 ring | Same |
| `0x08` | 1 | Ch3 ring | Same |
| `0x09` | 1 | Ch4 ring | Same |
| `0x0a` | 4 | Channel number digits | color[i] = ch(i+1) digit |
| `0x10` | 8 | Fader base strips | 2 LEDs per channel; color[2i] and color[2i+1] = ch(i+1) |
| `0x20` | 2 | Mic-mute ring | color[0] = active (unmuted), color[1] = muted |
| `0x21` | 2 | Bleep button | color[0] = inactive, color[1] = active |

Known b6 values by LED count: `0x1b` for 1 or 8 LEDs, `0x1e` for 2 LEDs, `0x24` for 4 LEDs.

Setting ch1 green, others red:

```
07 00 1f 00 00 00 1b 0f 02 00 06 01 00 00 01  00 ff 00  00...
07 00 1f 00 00 00 1b 0f 02 00 07 01 00 00 01  ff 00 00  00...
07 00 1f 00 00 00 1b 0f 02 00 08 01 00 00 01  ff 00 00  00...
07 00 1f 00 00 00 1b 0f 02 00 09 01 00 00 01  ff 00 00  00...
```

Source: `lighting-channel-mute_effect_static_ch1-green.pcapng` and related captures.

---

#### Sub-command C -- spectrum cycling (byte7=`0x0f`, byte8=`0x02`, effect=`0x03`)

```
07 00 1f 00 00 00 18  0f  02  00  <addr>  03  00  00  00  00...
```

`b6=0x18`, `effect=0x03`, no color data. Works on any zone.

Source: `lighting-wordmark_effect_spectrum-cycling.pcapng`, `lighting-channel-number_effect_spectrum-cycling.pcapng`.

---

#### Sub-command D -- mute state feedback (byte7=`0x08`)

Sent by the host to update mute LEDs on the device. Send this after handling a mute button press so the LED matches the new software state.

```
07 00 1f 00 00 00 03  08  10  00  <ch>  <muted>  00...
 0  1  2  3  4  5  6   7   8   9   10      11
```

| ch   | Channel |
|------|---------|
| 0x01 | Ch 1    |
| 0x02 | Ch 2    |
| 0x03 | Ch 3    |
| 0x04 | Ch 4    |
| 0x05 | Mic     |

`muted`: `0x00` = active (LED off), `0x01` = muted (LED on).

Synapse sends both states back-to-back when toggling; just send the new state.

Source: `mute-ch{1,2,3,4}_synapse.pcapng`, `mute-mic_synapse.pcapng`.

---

## Interrupt-IN path (EP 0x84) -- Report 0x09

Pushed at roughly 62 Hz when fader or button state changes. 8-byte payload.

```
09  <mute_mask>  <??> <f1>  <f2>  <f3>  <f4>  05
 0       1         2    3     4     5     6     7
```

| Byte | Field | Notes |
|------|-------|-------|
| 0 | Report ID | Always `0x09` |
| 1 | Mute bitmask | bit0=ch1, bit1=ch2, bit2=ch3, bit3=ch4, bit5=mic |
| 2 | Unknown | Changes during fader sweeps; purpose unclear |
| 3 | Fader 1 | `0x02` to `0x64` |
| 4 | Fader 2 | Same range |
| 5 | Fader 3 | Same range |
| 6 | Fader 4 | Same range |
| 7 | Constant | Always `0x05` |

Fader range is `0x02` to `0x64` (2 to 100 decimal). The hardware physically bottoms out at 2, not 0; map to a 0-100% scale as `(val - 2) / 98.0 * 100`.

The mute bits reflect button-held state, not toggle state. A rising edge (0 to 1 transition) means the button was just pressed; use edge detection to drive a logical mute toggle.

Source: `fader{1,2,3,4}_min_to_max.pcapng`, `mute_ch{1,2,3,4}.pcapng`, `mute_mic.pcapng`.

---

## DSP write sequence

Synapse follows this pattern every time it writes DSP settings:

1. For each parameter: send Report 0x04 (zone select) then Report 0x13 (parameter write), both using the same addr.
2. Flush: repeat step 1 for addr `0x54`, then `0x58`, then `0x5c`, all with zero val bytes.

The flush must happen after every DSP block or the device discards the writes.

---

## Open questions

- **Report 0x09 byte 2**: changes during fader sweeps but the correlation is unclear. Possibly a sum or checksum of fader values.
- **Mic gain dB scale**: 0x00 to 0x45 observed (69 steps); Synapse labels up to 68 dB but the mapping curve is not confirmed.
- **Gate threshold/reduction units**: values 0x0001 to 0x0400; unclear whether these are dB, percent, or a device-specific scale.
- **Minimum gate attack time**: sweeps start at 0x0027 (~39 ms) despite the capture label saying 0. The true minimum may not be zero.
- **Mic mute via Report 0x03**: Synapse sends a modified Report 0x03 payload when muting the mic. Not decoded yet.
- **Report 0x13 at addr 0x10**: appears in `mute-ch1_synapse.pcapng` with non-zero type bytes. Purpose unknown; do not send without further analysis.
- **Lighting b6 byte**: the relationship to LED count is partially understood. Use observed values from captures exactly; do not compute.
