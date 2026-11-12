# Shmoice
A voice command application that will send commands to do anything you want (for the most part). The user can create new commands either directly or by letting an AI model interpret what the command should do through user prompt.

Say the wake word, then a command: *"computer, next song"*. Each command is a list of steps that run in order.

## What a command can do

- **Media, volume and microphone**: play, pause, skip, set an exact volume, mute the mic.
- **Keyboard, mouse and text**: press or hold shortcuts, type or paste text, click, scroll.
- **Browser, apps and windows**: open sites and apps, search, switch tabs, snap, close or force quit.
- **System and power**: screenshots, dark mode, lock, sleep, shut down.
- **Listen for words**: take whatever you say next and use it in later steps as `{heard}`.
- **Advanced**, for building your own integrations:
  - **Send web request (HTTP)**: call a webhook, smart-home hub or API.
    `POST https://example.com/hook | Authorization: Bearer abc | {"text": "{heard}"}`
  - **Pick value from result**: pull one value out of a JSON reply, e.g. `main.temp`.
  - **Run PowerShell script**: run a script and use what it prints.
  - **Ask Claude**: ask a question and say, type or paste the answer.
  - **Continue only if…**: stop unless a condition holds, e.g. `{app} is chrome` or `{time} >= 18:00`.
  - **Run another command**: build big commands out of small ones.
  - **Add line to file**: keep a notes or log file.

Steps can use `{heard}`, `{result}` (the last web reply, script output or answer), `{clipboard}`, `{date}`, `{time}` and `{app}` in their parameter.

## Also included

- **AI Builder**: describe a command in plain words and Claude writes the steps (needs your own Anthropic API key).
- **Profiles**: commands that only run while certain apps are in front.
- **Schedules**: run commands at a time of day or every so many minutes.
- **History** and **Voice test** to see what was heard and why something did or didn't run.

The Help tab inside the app explains each of these, with a searchable list of every action.

## Building

Requires the .NET 10 SDK on Windows.

```
dotnet build Shmoice/Shmoice/Shmoice.csproj
```

`Shmoice/publish.ps1` builds the single-file exe and the installer.
