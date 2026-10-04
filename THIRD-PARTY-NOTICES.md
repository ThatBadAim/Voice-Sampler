# Third-Party Notices

VoiceScan includes code derived from the projects below. Their licenses require these notices to accompany
source and binary distributions.

## WebRTC voice activity detector

`engine/VoiceScan.Core/WebRtcVad.cs` is a C# port of the WebRTC VAD (`common_audio/vad` and the
signal-processing helpers it uses) from https://webrtc.googlesource.com/src.

```
Copyright (c) 2011, The WebRTC project authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google nor the names of its contributors may
    be used to endorse or promote products derived from this software
    without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Feature front-ends

`engine/VoiceScan.Core/SpeechFeatures.cs` reimplements the feature extraction of SpeechBrain
(`speechbrain.lobes.features.Fbank`, Apache License 2.0, https://github.com/speechbrain/speechbrain) and of
NVIDIA NeMo (`FilterbankFeatures`, Apache License 2.0, https://github.com/NVIDIA/NeMo) so that the bundled
ECAPA-TDNN and TitaNet models receive the input they were trained on. No source code from either project is
included. Model weight licenses are listed in `docs/LICENSES.md` and `models/manifest.json`.
