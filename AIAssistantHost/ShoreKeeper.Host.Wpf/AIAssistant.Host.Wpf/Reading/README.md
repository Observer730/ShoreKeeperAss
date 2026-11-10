# Reading（听书 M1）

命名空间 `AIAssistant.Host.Wpf.Reading`。这一层只使用 .NET 基础库，不引用 WPF 或 NAudio，测试工程可以直接链接这些源文件。

- `BookParser`：读取 `.txt` / `.epub`，输出章节纯文本。`Book.Id` 是源文件全部字节 SHA256 的小写十六进制前 24 位。
- `BookSegmenter`：按句切段，每段最多 200 个 UTF-16 字符。
- `TtsCache`：音频落在 `<root>\<bookId>\<segmentIndex>.mp3`，同目录的 `<segmentIndex>.key` 必须等于 `SHA256(UTF8(bookId|segmentIndex|voiceId|text))` 才算命中。

运行自测：

```powershell
dotnet run --project Reading.Tests -c Release
dotnet run --project Reading.Tests -c Release -- --book <txt-or-epub>
```
