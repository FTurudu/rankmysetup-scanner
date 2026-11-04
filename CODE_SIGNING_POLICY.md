# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

Only `RankMySetupScanner.exe` release builds produced by the GitHub Actions workflow in this repository from a tagged commit. Nothing is signed from a developer machine.

## Team roles

- Committers and reviewers: repository owner
- Approvers: repository owner

All team members use multi-factor authentication on GitHub and SignPath.

## Privacy

This program sends hardware information (CPU, graphics card and video memory, RAM modules and speeds, motherboard and BIOS version, drives, display resolution and refresh rate, monitor model, connected mouse / keyboard / headset models, Windows version, power plan, Game Mode and GPU scheduling settings, battery health on laptops and a roughly 20-second CPU and memory test result) to https://rankmysetup.com only when the user starts a scan. It never collects files, passwords, browsing history, computer name, user name or serial numbers.
