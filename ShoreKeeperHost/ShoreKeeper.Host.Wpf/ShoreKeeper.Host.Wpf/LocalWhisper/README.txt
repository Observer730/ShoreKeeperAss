Local Whisper setup

Put a whisper.cpp Windows executable here:
- whisper-cli.exe
- or main.exe

Put a ggml model under:
- LocalWhisper/models/

Recommended first model:
- ggml-base.bin
- or ggml-small.bin if your machine can handle it

The app searches these model names first:
- ggml-small.bin
- ggml-base.bin
- ggml-tiny.bin

Generated transcript txt files are written to:
- LocalWhisper/output/
