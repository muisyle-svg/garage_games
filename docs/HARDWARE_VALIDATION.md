# Hardware validation checklist

- Flash the release master, standard station, and custom prompt-demo images.
- Verify each XIAO ESP32-C3 registers with a stable hardware ID and a new boot ID
  after reset.
- Exercise 20 simulated or physical stations with rapid simultaneous presses.
- Confirm local feedback is immediate and controller/display updates stay below
  500 ms in normal venue conditions.
- Introduce packet loss and duplication; every acknowledged press must appear once.
- Change the venue Wi-Fi channel and confirm stations rediscover the master beacon.
- Reboot each component and perform controller-outage and Google-offline drills.
- Measure battery draw against a 2025 ESP-NOW-only receiver under the same test
  duration and LED duty cycle.
- Complete two dress rehearsals with the real laptop, monitor, master, receivers,
  power supplies, and venue network.

Record date, firmware release, hardware IDs, battery measurements, observed latency,
failures, and corrective action. Release only when every blocking item has an owner
and resolution.

