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


def decode_pcm(audio_path, gain_db=0.0):
    """用 ffmpeg 解成16kHz 单声道 s16le PCM，返回 float32 numpy。
    gain_db > 0 时做音量放大（B 站有些视频音轨本身很轻，
    RMS 只有 0.01 左右，会被 VAD 当成静音整段过滤掉）。"""
    os.makedirs(TMP, exist_ok=True)
    pcm = os.path.join(TMP, os.path.basename(audio_path) + '.pcm')
    cmd = ['ffmpeg', '-y', '-i', audio_path, '-ar', '16000', '-ac', '1',
           '-f', 's16le']
    if gain_db:
        cmd += ['-af', 'volume=%ddB' % gain_db]
    cmd += [pcm]
    subprocess.run(cmd, check=True, capture_output=True)
    raw = np.fromfile(pcm, dtype=np.int16)
    return raw.astype(np.float32) / 32768.0


def main():
    audio = sys.argv[1]
    out = sys.argv[2]
    lang = sys.argv[3] if len(sys.argv) > 3 else 'zh'
    # 第4 个参数可选：音量增益 dB（音轨轻时用 20~30）
    gain = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0

    print('decoding pcm (gain=%ddB)...' % gain, flush=True)
    pcm = decode_pcm(audio, gain)
    rms = float(np.sqrt((pcm ** 2).mean()))
    print('pcm seconds: %.1f  rms: %.4f' % (len(pcm) / 16000, rms), flush=True)

    print('loading model...', flush=True)
    model = WhisperModel('small', device='cpu', compute_type='int8',
                         local_files_only=True)

    # 【关键】VAD（语音活动检测）在这里帮倒忙：
    # B 站有些视频的音轨是**自动生成的字幕音轨**（无人声、只有朗读/静音交替），
    # VAD 会把整段判成静音 -> segs = 0。
    # 实测：vad=True -> 0 段，vad=False -> 正常识别。
    # 故默认关闭 VAD；只有在音频很长且含大量静音时才考虑开启。
    use_vad = False
    if len(sys.argv) > 5:
        use_vad = sys.argv[5].lower() == 'vad'
    print('transcribing (vad=%s)...' % use_vad, flush=True)
    segs, info = model.transcribe(pcm, language=lang, vad_filter=use_vad,
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