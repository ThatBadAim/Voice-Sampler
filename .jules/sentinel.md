## 2023-10-27 - [Command/Argument Injection via string interpolation in ProcessStartInfo]
**Vulnerability:** Command/Argument injection vulnerability found in `engine/VoiceScan.Core/AudioDecoder.cs` where `ProcessStartInfo.Arguments` was constructed using string interpolation with untrusted input (`mediaFilePath`). This could allow an attacker to inject arbitrary flags or commands into the FFmpeg/FFprobe invocations.
**Learning:** `ProcessStartInfo.Arguments` is parsed by the OS/runtime and is susceptible to injection if paths contain quotes or spaces that break the command string boundaries.
**Prevention:** Always use `ProcessStartInfo.ArgumentList.Add()` to pass arguments to external processes safely. The runtime handles the escaping automatically.
