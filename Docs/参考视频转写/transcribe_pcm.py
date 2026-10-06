"""绕过 faster-whisper 1.2.1 × av 19 不兼容问题的转写脚本。

问题：faster_whisper 1.2.1 内部调 av.open(..., metadata_errors="ignore")，
而 av 19 已移除该参数 -> TypeError。

解法：先用 ffmpeg 把音频解成 16kHz s16le PCM，
再用 numpy 读成 float32 喂给模型（模型接受 numpy 数组）。

用法：python transcribe_pcm.py <音频> <输出txt> [语言]
"""
import subprocess
import sys
import os
import tempfile

import numpy as np
from faster_whisper import WhisperModel

TMP = os.path.join(os.environ.get('LOCALAPPDATA', tempfile.gettempdir()), 'Temp', 'wb')


def decode_pcm(audio_path):
    """用 ffmpeg 解成16kHz 单声道 s16le PCM，返回 float32 numpy。"""
    os.makedirs(TMP, exist_ok=True)
    pcm = os.path.join(TMP, os.path.basename(audio_path) + '.pcm')
    subprocess.run(
        ['ffmpeg', '-y', '-i', audio_path, '-ar', '16000', '-ac', '1',
         '-f', 's16le', pcm],
        check=True, capture_output=True)
    raw = np.fromfile(pcm, dtype=np.int16)
    return raw.astype(np.float32) / 32768.0


def main():
    audio = sys.argv[1]
    out = sys.argv[2]
    lang = sys.argv[3] if len(sys.argv) > 3 else 'zh'

    print('decoding pcm...', flush=True)
    pcm = decode_pcm(audio)
    print('pcm seconds: %.1f' % (len(pcm) / 16000), flush=True)

    print('loading model...', flush=True)
    model = WhisperModel('small', device='cpu', compute_type='int8',
                         local_files_only=True)

    print('transcribing...', flush=True)
    segs, info = model.transcribe(pcm, language=lang, vad_filter=True,
                                  beam_size=5)
    segs = list(segs)
    print('segs =', len(segs), flush=True)

    with open(out, 'w', encoding='utf-8') as f:
        for s in segs:
            line = '[%02d:%02d-%02d:%02d] %s' % (
                int(s.start) // 60, int(s.start) % 60,
                int(s.end) // 60, int(s.end) % 60, s.text.strip())
            f.write(line + '\n')
    print('DONE ->', out, flush=True)


if __name__ == '__main__':
    main()