## GitHub
- Never push .env files or anything with sensitve keys

## C#
- Fix comments to be properly following generic formats, do not parse it like JS
- Comments should always remain concise and only placed atop functions
- Do NOT use XML comments, use plain text comments
- Do NOT double space between functions

## Code Style

- Keep all file names appropriately named after the project or relevant function use-cases
- Never use non-ASCII characters in code (no em dashes, smart quotes, fancy arrows, etc.) -- use only plain ASCII
- Never add comments within the function or for splitting up functionality unless deemed overly complex
- Never leave functions with an open-ended 'options' parameters, function inputs should always be defined in the actual function as a standalone variable
- Always organize '_functions' at the bottom of the file
- Always declare functions with specific parameters within scope
- Always add type docs that includes parameters, a return, and short one-line description to all functions
- Always add type docs to unidentified variables
- Always use '???' instead of 'Unknown' when applicable