# Dog Feeder Reminder

This is a quick-and-dirty, mostly AI generated, dog feeder reminder for the [Meadow F7 MCU](https://developer.wildernesslabs.co/Hardware/Reference/Meadow_Hardware/Meadow_F7/F7v2/) by [Wilderness Labs](https://www.wildernesslabs.co/).

It helps our familiy (mostly the children!) prevent overfeeding the dog by keeping track of a day and night feeding using a couple of LEDs. It also draws the kids' attention to feeding time by blinking the lights.

It can also send push notification reminders at the end of the feeding window if nobody fed. It also exposes status via a web page and/or a MQTT topic, which can be then shown in the Home Assistant dashboard.

A 3D printable case is also available in the root (3mf file).

Set WiFi credentials using `meadow device config wifi --ssid SSID --passcode PASSWORD`, make any needed changes in `app.config.yaml`, and then use `meadow run app` to deploy to the MCU.

### Home Assitant configuration

1. Add Mosquitto add-on
2. Configure add on; add a login
3. Start and ensure it loads
4. Devices -> Add Integration -> MQTT
5. Verify it's there
6. Edit `/homeassistant/configuration.yaml` (use File Editor add-on)
7. Check config and then reload HA
8. Add card to dashboard (manual), paste in YML

```
mqtt:
  sensor:
    - name: Dog Feeder Morning
      unique_id: dog_feeder_morning
      state_topic: dog-feeder/feeding/morning-fed
      value_template: "{{ 'Fed' if value == 'true' else 'Not Fed' }}"

    - name: Dog Feeder Evening
      unique_id: dog_feeder_evening
      state_topic: dog-feeder/feeding/evening-fed
      value_template: "{{ 'Fed' if value == 'true' else 'Not Fed' }}"

    - name: Dog Feeder Vacation
      unique_id: dog_feeder_vacation
      state_topic: dog-feeder/vacation-mode
      value_template: "{{ 'On' if value == 'true' else 'Off' }}"

    - name: Dog Feeder Raw Status
      unique_id: dog_feeder_raw_status
      state_topic: dog-feeder/status
      value_template: "{{ value_json.morning_fed }}"
      json_attributes_topic: dog-feeder/status

  button:
    - name: Dog Feeder Mark Morning
      unique_id: dog_feeder_mark_morning
      command_topic: dog-feeder/commands/feeding
      payload_press: morning

    - name: Dog Feeder Mark Evening
      unique_id: dog_feeder_mark_evening
      command_topic: dog-feeder/commands/feeding
      payload_press: evening

    - name: Dog Feeder Mark Both
      unique_id: dog_feeder_mark_both
      command_topic: dog-feeder/commands/feeding
      payload_press: both

    - name: Dog Feeder Reset Feedings
      unique_id: dog_feeder_reset_feedings
      command_topic: dog-feeder/commands/feeding
      payload_press: reset

    - name: Dog Feeder Advance State
      unique_id: dog_feeder_advance_state
      command_topic: dog-feeder/commands/feeding
      payload_press: advance

  switch:
    - name: Dog Feeder Vacation Mode
      unique_id: dog_feeder_vacation_mode
      command_topic: dog-feeder/commands/vacation
      payload_on: on
      payload_off: off
      state_topic: dog-feeder/vacation-mode
      state_on: "true"
      state_off: "false"
```

```
type: entities
title: Dog Feeder
show_header_toggle: false
entities:
  - entity: sensor.dog_feeder_morning
    name: Morning
    icon: mdi:weather-sunset-up
  - entity: sensor.dog_feeder_evening
    name: Evening
    icon: mdi:weather-night
  - entity: sensor.dog_feeder_vacation
    name: Vacation
    icon: mdi:palm-tree
  - type: section
    label: Controls
  - entity: switch.dog_feeder_vacation_mode
    name: Vacation Mode
  # - entity: button.dog_feeder_mark_morning
  #   name: Mark Morning
  # - entity: button.dog_feeder_mark_evening
  #   name: Mark Evening
  # - entity: button.dog_feeder_mark_both
  #   name: Mark Both
  # - entity: button.dog_feeder_reset_feedings
  #   name: Reset Feedings
  - entity: button.dog_feeder_advance_state
    name: Feed Toggle
```