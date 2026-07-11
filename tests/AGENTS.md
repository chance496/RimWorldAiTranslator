# Regression test instructions

- Tests must use synthetic fixtures copied below a unique `%TEMP%` directory.
- Never read or write the real `%LOCALAPPDATA%\RimWorldAiTranslator`, Workshop mods, RMK subscription, or user API keys.
- Network access is forbidden in the default regression suite. Provider behavior must use a loopback fake server.
- Every test must clean only its verified temporary root and return a nonzero exit code on failure.
- Assertions must verify output content and preservation properties, not only process exit codes.
