Whisperer lets you generate subtitles for any kind of video/audio files.

It uses OpenAI's whisper, it runs as many instances simultaneously as your Nvidia GPU's memory allows.

First make sure whisper is in your path and runs with --device cuda, so Nvidia cards only as I don't have an AMD card to test with.

You also need ffmpeg.exe in your path, not included.

With an RTX 3080 (10 GB) it runs 4 instances at a time with base model. You can choose which model from a drop down.


