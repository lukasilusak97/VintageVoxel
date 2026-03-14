---
description: When editing files or creating new files
# applyTo: 'Describe when these instructions should be loaded by the agent based on task context' # when provided, instructions will automatically be added to the request context when the pattern matches an attached file
---

<!-- Tip: Use /create-instructions in chat to generate content with agent assistance -->

> **GLSL Encoding Instruction:**
> * **DO NOT** use non-ASCII/Unicode characters anywhere in GLSL files.
> * **THIS INCLUDES COMMENTS.** Even hidden characters or styled punctuation (like smart quotes, em-dashes `—`, or Unicode arrows `→`) will break the compiler.
> * **INSTEAD,** always use standard ASCII equivalents (e.g., use `--` for dashes, `->` for arrows, and standard `-` for boxes/lines).
> * **Why:** GLSL compilers only accept standard ASCII source files.