#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""历史错误自检 —— 每次改动后跑一遍，检查有没有重犯旧错。

★ 用户要求（2026-10-08）：
  「现在包括之后的所有改动之后加自检，检查是否翻过之前犯过的错误，
    这个每一次任务都要做」

用法：
    python .workbuddy/tools/check_regressions.py      # 只报 ERROR
    python .workbuddy/tools/check_regressions.py -v   # 连WARN 一起报

设计原则（来自本项目血泪教训）：
    ★ 「窄而准」>「宽而乱」—— 误报多了就没人看，等于没检查。
      本脚本只报**确凿的**问题，不做启发式猜测。
"""
import os
import re
import sys

# ★ 注意：Python 3.13 的 os 模块**没有** os.dirname / os.abspath，
#   只能用 os.path.dirname / os.path.abspath。
#   （写成 os.dirname 会报 "module 'os' has no attribute 'dirname'"）
_HERE = os.path.dirname(os.path.abspath(__file__))   # .workbuddy/tools
ROOT = os.path.dirname(os.path.dirname(_HERE))      # 项目根
os.chdir(ROOT)

SILK = 'Assets/Silk/SilkBuilder.cs'
HOUSE = 'Assets/Silk/HouseBlockout.cs'

ERRORS = []
WARNINGS = []


def err(m):
    ERRORS.append(m)


def warn(m):
    WARNINGS.append(m)


def read(path):
    with open(path, encoding='utf-8') as f:
        return f.read()


def strip_comments(src):
    """剥离注释，但保留行数（等长空格替换）。"""
    src = re.sub(r'/\*.*?\*/',
                 lambda m: re.sub(r'[^\n]', ' ', m.group(0)), src, flags=re.S)
    src = re.sub(r'//[^\n]*', lambda m: ' ' * len(m.group(0)), src)
    return src


def class_ranges(path):
    """返回 [(类名, 起始行, 结束行)]，按 1-based。

    ★ 教训（CS0103 第 6 次）：同一文件里有多个类时，
      按行号猜插入点会插错类。必须先算出每个类的真实范围。
    """
    src = read(path)
    lines = src.split('\n')
    starts = []
    for i, ln in enumerate(lines):
        #★ 必须 ln.strip()：类声明可能是缩进的（嵌套类），
        #   不 strip 会漏匹配，导致「类范围」算错。
        #   （实测踩过：SilkBuilder 的范围被算成 0 行，检查项静默失效）
        m = re.match(r'\s*(?:public |internal |private |static |partial )*'
                     r'class (\w+)', ln.strip())
        if m:
            starts.append((i + 1, m.group(1)))
    out = []
    for idx, (ln, name) in enumerate(starts):
        end = starts[idx + 1][0] - 1 if idx + 1 < len(starts) else len(lines)
        out.append((name, ln, end))
    return out


def owner_of(path, member):
    """返回成员所属的类名；找不到返回 None。"""
    ranges = class_ranges(path)
    src = read(path)
    for i, ln in enumerate(src.split('\n')):
        if re.search(r'\b%s\b' % re.escape(member), ln) and \
           re.search(r'\b(public|internal|private|static)\b', ln):
            for name, a, b in ranges:
                if a <= i + 1 <= b:
                    return name
    return None


# =====================================================================
# 错误 1（CS0103 #1~#6）：成员归属 —— 字段与方法必须同类
# =====================================================================
def check_member_ownership():
    """【已放弃】检测「成员被跨类引用」。

    ★ 为什么放弃：本项尝试后产生 **226 条误报**，全部是噪音：
        - SilkBuilder / SilkParkourController 等类**本来就在同一个文件里**，
          互相引用类型名（VoxelGrid、SilkChain、AnchorPoint...）完全合法
        - 局部变量名（target / from / to / color）被当成成员名
        - 参数名与成员名同名，无法区分

      这与 selfcheck 第 22 项（CS0103 自动检测）失败的原因**完全相同** ——
      C# 的作用域需要真正的语法分析（Roslyn），文本正则做不到。

    ★ 可靠的替代：**看 Unity Console 的编译报错**。
      CS0103 / CS1061 会给出精确行号，比任何启发式都准。
      本项目 6 次 CS0103 全部是编译器发现的，不是脚本发现的。
    """
    pass


# =====================================================================
# 错误 2：局部 const 被其他方法引用
# =====================================================================
def check_local_const_leak():
    """BuildStair 的局部 const stairW 被 BuiltMessage 引用（真实犯过）。"""
    if not os.path.exists(HOUSE):
        return
    src = read(HOUSE)
    clean = strip_comments(src)
    lines = clean.split('\n')
    ranges = class_ranges(HOUSE)

    # 找深度 >= 8 的 const 声明（方法体内的）
    for i, ln in enumerate(lines):
        m = re.match(r'\s{8,}const\s+[\w<>\[\]\.]+\s+(\w+)\s*=', ln)
        if not m:
            continue
        name = m.group(1)
        # 找方法边界（4 空格缩进的闭括号）
        end = None
        for j in range(i + 1, len(lines)):
            if re.match(r'^    \}\s*$', lines[j]):
                end = j
                break
        if end is None:
            continue
        # 在方法外找裸引用
        for j in range(end + 1, len(lines)):
            if re.match(r'^    [a-zA-Z]', lines[j]):
                break   # 进入下一个成员，停止
            if re.search(r'(?<![\w.])%s\b' % re.escape(name), lines[j]):
                err('%s: 局部 const %s 声明在第 %d 行的方法内，'
                    '却被第 %d 行（方法外）引用 -> CS0103'
                    % (os.path.basename(HOUSE), name, i + 1, j + 1))
                break


# =====================================================================
# 错误 3：推导量写成字面量（改了源头忘了改结果）
# =====================================================================
def check_derived_literals():
    """RoomLength / StepRise / 起点高度必须是公式，不能硬编码。

    ★ 教训：RoomLength曾硬编码 (4.5*5.0)，改 RoomSize 而房间不变；
      StepRise 曾硬编码 0.16，改层高后楼梯到不了二层；
      起点高度曾硬编码 -29.5，球径缩小后悬空 2 格。
    """
    if not os.path.exists(HOUSE):
        return
    h = read(HOUSE)

    m = re.search(r'public const float RoomLength = ([^;]+);', h)
    if m and 'RoomSize' not in m.group(1):
        err('HouseBlockout: RoomLength = %s 是字面量，'
            '必须写成 (RoomSize * 5.0)' % m.group(1).strip())

    m = re.search(r'StepRise\s*(?:=>|=)\s*([^;]+);', h)
    if m:
        v = m.group(1).strip()
        if 'StoreyHeight' not in v or 'TotalSteps' not in v:
            err('HouseBlockout: StepRise = %s 必须写成 '
                'StoreyHeight / TotalSteps' % v)

    if not os.path.exists(SILK):
        return
    s = read(SILK)

    # 起点高度
    if 'StartPositionBasicStage' not in s:
        err('SilkBuilder: 缺少 StartPositionBasicStage（起点高度应写成公式）')
    elif not re.search(r'BasicStageFloorZ\s*\+\s*visualRadius', s):
        err('SilkBuilder: StartPositionBasicStage 没用 '
            'BasicStageFloorZ + visualRadius 公式')

    # 相机距离必须随球径缩（否则球像一粒米）
    vr = re.search(r'public float visualRadius = ([\d.]+)f', s)
    cd = re.search(r'public float camDistance = ([\d.]+)f', s)
    if vr and cd:
        ratio = float(cd.group(1)) / (float(vr.group(1)) * 2)
        if ratio > 2.0:
            err('SilkBuilder: 相机/球直径 = %.2f（%.1f/%.1f），'
                '超过 1.6~2.0 -> 球在画面里太小'
                % (ratio, float(cd.group(1)), float(vr.group(1)) * 2))


# =====================================================================
# 错误 4：菜单/路径类的人为失误（我今晚错了 3 次）
# =====================================================================
def check_hardcoded_mistakes():
    """检查代码注释里那些「我说错过」的路径与版本号是否被改回来。"""
    for path in ('Assets/Silk/HouseBlockout.cs', SILK):
        if not os.path.exists(path):
            continue
        src = read(path)

        # 4a. 住宅相机的退让距离不能太大（房间半宽仅 17.5 格）
        #     ★ 只查 HouseBlockout.CameraDistance —— 不查控制器默认值，
        #       因为 OpenStageCameraDistance = 20 是**空旷关卡**的合法值。
        #     （初版误报过：把 OpenStageCameraDistance 当成住宅值）
        if os.path.basename(path) == 'HouseBlockout.cs':
            m = re.search(r'CameraDistance\s*=\s*([\d.]+)f', src)
            if m and float(m.group(1)) > 12:
                err('HouseBlockout: CameraDistance = %.1f 格太大 —— '
                    '住宅房间半宽仅 17.5 格，相机会退到墙外'
                    % float(m.group(1)))

        # 4b. 门洞不能回到加宽值（球径已缩回 2.5 格，国标够用）
        #     ★ 排除 MainDoorWidth —— 它本来就是 1.10（规范 >=1.00，合规）
        if os.path.basename(path) == 'HouseBlockout.cs':
            for name in ('BedroomDoorWidth', 'KitchenDoorWidth',
                         'ToiletDoorWidth'):
                m = re.search(r'%s = ([\d.]+)f' % name, src)
                # ★ 阈值要卡在国标之上、偏离值之下：
                #   国标 0.70/0.80/0.90  -> 合法
                #   偏离 1.10-> 应报错
                # 用 1.0 作阈值（而不是 1.2）才能抓到 1.10
                if m and float(m.group(1)) > 1.0:
                    err('HouseBlockout: %s = %.2f 米 —— '
                        '球径已缩到 2.5 格，国标 0.70~0.90 已够用，'
                        '不必加宽到 1.10' % (name, float(m.group(1))))


# =====================================================================
# 错误 4b：跨类字段访问的方向（今晚连续踩了 3 次）
# =====================================================================
def check_cross_class_refs():
    """★ 本项目已因此报CS0103/CS1061 共 3 轮、约 80 个错误。

    【核心规则】SilkBuilder 与 SilkParkourController 互相持有引用：
        SilkParkourController 持有 `SilkBuilder builder`
        但 SilkBuilder **不持有**控制器 -> 方向是单向的
    所以：
      · 控制器里访问配置字段 -> 必须写 `builder.xxx`
      · 控制器里访问自己的字段 -> 必须**裸写**（不加 builder.）
      · SilkBuilder 里访问控制器的字段/方法 -> 编译不过（根本不该出现）
    """
    if not os.path.exists(SILK):
        return
    src = read(SILK)
    lines = strip_comments(src).split('\n')

    # 定位两个类的范围
    ranges = class_ranges(SILK)
    ctrl = None
    builder = None
    for name, a, b in ranges:
        if name == 'SilkParkourController':
            ctrl = (a, b)
        elif name == 'SilkBuilder':
            builder = (a, b)
    if not ctrl:
        return
    ca, cb = ctrl

    # SilkBuilder 的配置字段（只列我们新增的这几个，避免噪音）
    cfg = ('freeBuildMode', 'freeBuildSearchMaxZ', 'freeBuildSpawnOverride')

    for i in range(ca - 1, min(cb, len(lines))):
        s = lines[i]
        if not s.strip():
            continue
        # 控制器里访问配置字段必须有 builder. 前缀
        for name in cfg:
            if re.search(r'(?<![\w.])%s\b' % name, s):
                err('%d: 控制器里访问 %s 缺 `builder.` 前缀 -> CS0103'
                    % (i + 1, name))
        # 控制器的自有字段/方法不该加 builder. 前缀
        #★ 这条我连犯两次：先是字段，这次是 DetectFreeBuildSpawn 等方法。
        for f in ('visualRadius', 'fallRespawnZ', 'startPosition',
                  'DetectFreeBuildSpawn', 'EffectiveStartPosition',
                  'EffectiveFallRespawnZ', 'StartPositionBasicStage'):
            if 'builder.' + f in s:
                err('%d: %s 是控制器自有的，不该加 `builder.` 前缀'
                    % (i + 1, f))

    # ★ 反向检查：SilkBuilder 类里不该引用控制器的方法
    if builder:
        ba, bb = builder
        for i in range(ba - 1, min(bb, len(lines))):
            s = lines[i]
            for f in ('DetectFreeBuildSpawn', 'EffectiveFallRespawnZ',
                      'EffectiveStartPosition'):
                if f in s and 'public' not in s:
                    err('%d: SilkBuilder 里引用了控制器的 %s —— '
                        '但 SilkBuilder **不持有**控制器引用，编译不过'
                        % (i + 1, f))


# =====================================================================
# 错误 5：自检项本身的假阴性（检查写得不对）
# =====================================================================
def check_selfcheck_quality():
    """检查 selfcheck.py 里有没有「只数出现次数」这类弱检查。"""
    path = '.workbuddy/tools/selfcheck.py'
    if not os.path.exists(path):
        return

    src = read(path)

    # 5a. 计数式检查容易被「表达式变形」骗过
    #     （第 24c 条教训：数 thick 出现次数，被 thick*0.3f 骗过）
    for m in re.finditer(r"\.count\(['\"](\w+)['\"]\)\s*<\s*(\d+)", src):
        var, n = m.group(1), int(m.group(2))
        warn('selfcheck.py: 用 count("%s") < %d 判断 —— '
             '「只数标识符出现次数」会被表达式变形骗过'
             '（如thick*0.3f 里仍含 thick）。应匹配目标表达式本身。'
             % (var, n))

    # 5b. 截取范围过小（变异测试漏抓的原因）
    for m in re.finditer(r'\{0,(\d+)\}\?\)', src):
        n = int(m.group(1))
        if n < 1000:
            warn('selfcheck.py: 有一处正则只截取 %d 字符 —— '
                 '方法体里注释多时会被截断导致漏抓（变异测试踩过）' % n)


# =====================================================================
# 主流程
# =====================================================================
def main():
    verbose = '-v' in sys.argv

    check_local_const_leak()
    check_derived_literals()
    check_hardcoded_mistakes()
    check_cross_class_refs()
    check_selfcheck_quality()

    print('=' * 66)
    print('历史错误自检 —— 检查是否重犯旧错')
    print('=' * 66)

    if not ERRORS and not WARNINGS:
        print()
        print('[OK]未发现历史错误重犯')
    else:
        for m in ERRORS:
            print('  [ERR]  %s' % m)
        if verbose:
            for m in WARNINGS:
                print('  [WARN] %s' % m)
        elif WARNINGS:
            print()
            print('  （另有 %d 条提示，用 -v 查看）' % len(WARNINGS))

    print()
    print('-' * 66)
    print('ERROR %d | WARN %d' % (len(ERRORS), len(WARNINGS)))
    return 1 if ERRORS else 0


if __name__ == '__main__':
    sys.exit(main())
