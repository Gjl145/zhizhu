"""
深挖单个 UE .uasset：把「有意义的标识符」抽出来，滤掉引擎噪音
====================================================================
用途
----
`.uasset` 的 name table 是明文，能读出变量名/函数名/类名。
但 90% 是 UE 引擎内部符号（Verbs/Warnings/Materials/...），是噪音。

本脚本的价值在于**滤掉噪音后剩下的东西**—— 那往往就是：
  · 参数名（带阈值语义的，如 XxxThreshold / XxxDistance / XxxAngle）
  · 状态名（ClimbState / bIsClimbing / WallSide...）
  · 函数名（能看出作者把逻辑切成了哪几块）

★ 关键判断：抽不出数值。.uasset 里的 float 存的是二进制 IEEE754，
  没有字段偏移表就读不出「哪个 float 对应哪个变量」。
  → 所以本脚本给的是「作者关心哪些参数」，不是「参数是多少」。
  → 数值必须去 UE 里打开看，或者我们自己在 Unity 里实测定。

用法
----
python ue_asset_deep.py <文件.uasset> [--minlen 3]
"""

import sys
import os
import re
import struct
from collections import Counter, defaultdict


# ------------------------------------------------------- UE 引擎噪音词典

# 这些是 UE 引擎/编辑器内部符号，永远出现在每个 uasset 里，没有信息量
ENGINE_NOISE = re.compile(
    r'^(?:'
    r'Material|Materials|Texture|Textures|Sound|Shading|Static|'
    r'/Engine/|/Script/|Editor|Engine|'
    r'Verb\w*|Warning\w*|Error\w*|Log\w*|'
    r'Property|Function|Object|Struct|Enum|Class|Module|'
    r'Component|Actor|Timer|Vector|Vector2|Vector4|Rotator|Quat|Color|'
    r'Float|Integer|Bool|String|Name|Text|'
    r'Default|None|True|False|'
    r'PP\w*|PostProcess|Bloom|MotionBlur|LensFlare|'
    r'BP\w*_C|GeneratedClass|ClassDefaultObject|'
    r'\w*_Default|Default__\w*'
    r')$',
    re.IGNORECASE,
)

# 明显是引擎/编辑器词汇的子串
ENGINE_SUBSTR = (
    '/Engine/', '/Script/', 'EditorUtility', 'EditorOnly',
    'SourceControl', 'AssetRegistry', 'UnrealEd', 'Blutility',
    'Reimport', 'Reorder', 'FactoryCreateNew', 'FactoryCreate',
    'ST_', 'SWidget', 'SButton', 'STextBlock', 'SMenu', 'SWindow',
    'DetailsView', 'PropertyEditor', 'ClassViewer',
    'StaticMesh', 'SkeletalMeshComponent', 'BoxComponent',
    'SphereComponent', 'CapsuleComponent', 'ArrowComponent',
    'WidgetComponent', 'NiagaraComponent', 'ParticleSystem',
    'BlueprintGeneratedClass', 'WidgetBlueprintGeneratedClass',
)

# 我们的项目没用的子系统（用户明确说今天只做攀爬）
IRRELEVANT_SUBSYS = (
    'Swimming', 'WallRunning', 'Zipline', 'PushPull',
    'BeamWalk', 'SlidingSystem', 'NarrowPath',
)

# 有意义的后缀 —— 带这些的词通常是参数或状态
PARAM_SUFFIX = (
    'Threshold', 'Distance', 'Height', 'Angle', 'Speed', 'Force',
    'Radius', 'Range', 'Offset', 'Tolerance', 'Depth', 'Width',
    'Duration', 'Delay', 'Cooldown', 'Chance', 'Scale', 'Gravity',
    'Climb', 'Climbing', 'Ledge', 'Hang', 'Brace', 'Shimmy', 'Reach',
    'Grab', 'Release', 'Corner', 'Leap', 'Mantle', 'Vault',
)


def extract_strings(data, min_len=3):
    out = set()
    cur = bytearray()
    for b in data:
        if 32 <= b < 127:
            cur.append(b)
        else:
            if len(cur) >= min_len:
                out.add(cur.decode('ascii', 'ignore'))
            cur = bytearray()
    if len(cur) >= min_len:
        out.add(cur.decode('ascii', 'ignore'))
    return out


def is_noise(s):
    if len(s) < 3:
        return True
    # 路径类
    if any(sub in s for sub in ENGINE_SUBSTR):
        return True
    # 纯数字 / 哈希
    if re.fullmatch(r'[0-9A-Fa-f]+', s):
        return True
    # 引擎词典
    if ENGINE_NOISE.match(s):
        return True
    # UE 的对象引用形式 Foo.Bar_C_0 这类
    if re.search(r'_C_\d+$', s):
        return True
    # 含中文/乱码
    if not s.isascii():
        return True
    return False


def categorize(s):
    """给一个有意义的字符串打标签"""
    if any(k in s for k in PARAM_SUFFIX):
        return 'PARAM'          # 像参数
    if s.startswith(('bIs', 'bHas', 'bCan', 'bShould', 'bWas', 'bEnable', 'bUse', 'bWants')):
        return 'BOOL'           # 像开关
    if re.search(r'(State|Mode|Phase|Type|Status)$', s):
        return 'STATE'          # 像状态枚举
    if re.search(r'^(Get|Set|On|Handle|Try|Can|Is|Do|Update|Reset|Spawn|Trace|Find)[A-Z]', s):
        return 'FUNC'           # 像函数名
    return 'MISC'


def extract_floats(data):
    """扫一遍 IEEE754 float，找出「看起来像游戏参数」的值。

    ★ 这不能定位到具体变量（没有偏移表），但能给出「作者用了哪些量级的数」——
    例如是否出现 0.5 / 45.0 / 100.0 这种典型阈值。
    ★ 认不出来就认不出来，不编造归属。
    """
    vals = Counter()
    n = len(data)
    for i in range(0, n - 4):
        try:
            v = struct.unpack_from('<f', data, i)[0]
        except struct.error:
            break
        # 只保留「像人写的参数」的：正数、非NaN/Inf、量级合理
        if v != v or v in (float('inf'), float('-inf')):
            continue
        if v == 0.0:
            continue
        av = abs(v)
        if 0.001 <= av <= 100000:
            # 四舍五入到 3 位，减少浮点噪音
            key = round(v, 3)
            vals[key] += 1
    return vals


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    path = sys.argv[1]
    if not os.path.isfile(path):
        print(f'✗ 文件不存在: {path}')
        return 1

    with open(path, 'rb') as f:
        data = f.read()

    size_mb = len(data) / (1024 * 1024)
    print('=' * 74)
    print(f'  {os.path.basename(path)}   ({size_mb:.1f} MB)')
    print('=' * 74)

    all_str = extract_strings(data)
    meaningful = sorted(s for s in all_str if not is_noise(s))

    print()
    print(f'总字符串 {len(all_str):>6} → 滤掉引擎噪音后剩 {len(meaningful)}')
    print('=' * 74)

    groups = defaultdict(list)
    for s in meaningful:
        groups[categorize(s)].append(s)

    order = ('STATE', 'PARAM', 'BOOL', 'FUNC', 'MISC')
    titles = {
        'STATE': '★ 状态名（决定要做哪几个模式）',
        'PARAM': '★★★ 参数名（★ 作者关心哪些量★ 阈值就是从这里来的）',
        'BOOL': '布尔开关（功能开关 / 姿态判定）',
        'FUNC':  '函数名（作者把逻辑切成了哪几块）',
        'MISC':  '其它有意义标识符',
    }

    for g in order:
        items = groups.get(g, [])
        if not items:
            continue
        print()
        print(f'【{titles[g]}】 {len(items)} 个')
        print('-' * 74)
        # 长单词（通常是变量名）优先
        items.sort(key=lambda s: (-len(s), s))
        for s in items[:70]:
            print(f'  {s}')
        if len(items) > 70:
            print(f'  ... 另有 {len(items) - 70} 个')

    # 浮点参数
    floats = extract_floats(data)
    # 只显示出现次数 >= 3 的（单次出现多半是巧合）
    common = [(v, c) for v, c in floats.most_common(4000) if c >= 3]
    if common:
        print()
        print('【★ 常见浮点值（出现 >= 3 次）· 可能是阈值参数】')
        print('-' * 74)
        print('  ★ 注意：无法定位到变量名，只能看量级和「典型值」')
        pretty = ', '.join(f'{v:g}' for v, c in common[:60])
        print(f'  {pretty}')

    return 0


if __name__ == '__main__':
    sys.exit(main())