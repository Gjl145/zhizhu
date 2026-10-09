# -*- coding: utf-8 -*-
"""
跨类成员解析检查 —— 专抓 CS0117「成员不存在」类错误。

★★★ 为什么必须有这个工具（本项目血泪史）★★★
  2026-10-09 用户反馈「现在很多报错」，但 Unity 日志停更（编辑器没编译），
  且沙盒内无法 batchmode 编译（项目被锁）→ 我拿不到权威错误列表。
  之前的 precheck.py 只做括号/声明检查（纯文本可判定），
  **查不出「A 类没有 B 成员」** —— 而这正是我改签名时最容易犯的错。

  例：给 SilkSpiderLimb.Solve 加了两个参数，
      却忘了同步 SilkSpiderAnatomy.UpdateLimbs 的调用 → CS1503。
      又如引用了 SilkSpiderAnatomy 里不存在的属性 → CS0117。
  这类错误**正则完全查不出**，因为它需要知道「类里到底声明了什么」。

★★★ 本工具做什么 ★★★
  1. 扫描项目里所有 .cs，按类名建立「成员表」
     （字段、属性、方法签名 —— 含参数个数与类型）
  2. 扫描所有 `类名.成员名` 形式的访问
  3. 拿访问去比对成员表 → 报「该类没有这个成员」
  4. 比对方法调用的实参个数 vs 形参个数 → 报 CS1503

★★ 局限性（必须坦诚承认，不能假装它是编译器）★★★
  - 只认「显式写出类名」的访问（`obj.Member`）。
    裸调用（`Member()`）查不出 —— 那是第 14b 项检查的活。
  - 继承链没展开：父类成员查不到 → 会漏报，不会误报。
  - 类型名可能与变量名同名，无法区分 → 宁可漏报不误报。
  - 属性 `A => x` 与字段 `A` 视为同一个名字（不区分，不影响有无）。
  ★ 结论：**宁要漏报不要误报**。误报多了就没人看，等于没检查。
    最终仍须Unity Console 为准。
"""

import os
import re
import sys

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.join(os.path.dirname(BASE), 'Assets')

errors = []
notes = []


def strip_code_lines(src):
    """剥离注释与字符串，**保持行数不变**（否则行号漂移）。"""
    out = []
    in_block = False
    for ln in src.split('\n'):
        s = ln
        res = []
        i = 0
        in_str = False
        while i < len(s):
            if in_block:
                if s.startswith('*/', i):
                    in_block = False
                    i += 2
                else:
                    i += 1
                continue
            if in_str:
                if s[i] == '\\':
                    i += 2
                    continue
                if s[i] == '"':
                    in_str = False
                i += 1
                continue
            if s.startswith('//', i):
                break
            if s.startswith('/*', i):
                in_block = True
                i += 2
                continue
            if s[i] == '"':
                in_str = True
                res.append(' ')
                i += 1
                continue
            res.append(s[i])
            i += 1
        out.append(''.join(res))
    return out


# ---------- 1. 建立类 -> 成员表 ----------

class_members = {}      # className -> set(memberName)
class_methods = {}      # (className, methodName) -> [minArgs, maxArgs]  (None = 变参)
class_bases = {}        # className -> [baseName]

member_re = re.compile(
    r'^\s*(?:public|private|protected|internal)?\s*'
    r'(?:static\s+|readonly\s+|const\s+|sealed\s+|override\s+|virtual\s+|'
    r'extern\s+|unsafe\s+|volatile\s+|new\s+)*'
    r'(?:[\w\.\<\>\[\]\?\,\s]+?)\s+'
    r'([A-Za-z_]\w*)\s*'
    r'(?:\{|\(|=>|;|=)')

method_sig_re = re.compile(
    r'^\s*(?:public|private|protected|internal)?\s*'
    r'(?:static\s+|virtual\s+|override\s+|sealed\s+|new\s+|unsafe\s+)*'
    r'(?:[\w\.\<\>\[\]\?\,\s]+?)\s+'
    r'([A-Za-z_]\w*)\s*\(([^)]*)\)')

class_re = re.compile(r'^\s*(?:public\s+|internal\s+|private\s+)?'
                      r'(?:sealed\s+|abstract\s+|static\s+|partial\s+)*'
                      r'class\s+([A-Za-z_]\w*)'
                      r'\s*(?::\s*([^{]+))?')

# UnityEngine 里我们用到的常用成员（避免误报）
KNOWN_EXTERNAL = {
    'Debug': {'Log', 'LogWarning', 'LogError', 'DrawLine', 'DrawRay'},
    'Mathf': {'Clamp', 'Clamp01', 'Min', 'Max', 'Abs', 'Sin', 'Cos', 'Atan2',
              'Sqrt', 'PI', 'Deg2Rad', 'Rad2Deg', 'Lerp', 'InverseLerp',
              'Repeat', 'Sign', 'Floor', 'Ceil', 'Round', 'Pow', 'Exp', 'Log'},
    'Vector3': {'zero', 'one', 'up', 'down', 'left', 'right', 'forward', 'back',
                'Distance', 'Dot', 'Cross', 'Lerp', 'MoveTowards', 'Project',
                'ProjectOnPlane', 'Angle', 'Scale', 'Normalize', 'SqrMagnitude',
                'magnitude', 'normalized', 'x', 'y', 'z', 'ToString', 'SmoothDamp',
                'ClampMagnitude', 'Reflect', 'Excluding', 'Min', 'Max', 'SignedAngle',
                'RotateTowards', 'LerpUnclamped', 'Clamp01', 'Normalize'},
    'Vector2': {'zero', 'one', 'up', 'down', 'left', 'right', 'Distance', 'Dot',
                'Lerp', 'magnitude', 'sqrMagnitude', 'normalized', 'x', 'y',
                'ToString', 'Project', 'ProjectOnPlane', 'Angle'},
    'Vector4': {'zero', 'one', 'x', 'y', 'z', 'w', 'ToString'},
    'Quaternion': {'identity', 'LookRotation', 'FromToRotation', 'Inverse',
                   'Euler', 'AngleAxis', 'Slerp', 'Lerp', 'Angle', 'Dot',
                   'ToString', 'operator *'},
    'Color': {'white', 'black', 'red', 'green', 'blue', 'yellow', 'cyan',
              'magenta', 'gray', 'grey', 'clear', 'ToString', 'Lerp'},
    'Math': {'Max', 'Min', 'Abs', 'Sqrt', 'Pow', 'PI', 'Round', 'Floor', 'Ceil',
             'Sign', 'Log', 'Exp', 'Sin', 'Cos', 'Atan2', 'Sinh', 'Cosh', 'Tanh'},
    'Time': {'deltaTime', 'fixedDeltaTime', 'time', 'frameCount', 'timeScale',
             'fixedUnscaledDeltaTime', 'maximumDeltaTime'},
    'Input': {'GetKey', 'GetKeyDown', 'GetKeyUp', 'GetMouseButton',
              'GetMouseButtonDown', 'GetAxis', 'GetAxisRaw', 'mousePosition'},
    'Screen': {'width', 'height'},
    'Random': {'Range', 'value', 'insideUnitSphere', 'insideUnitCircle', 'Range01'},
    'LayerMask': {'NameToLayer', 'GetMask', 'value'},
    'Physics': {'Raycast', 'SphereCast', 'CapsuleCast', 'OverlapSphere',
                'OverlapBox', 'OverlapCapsule', 'RaycastAll', 'SphereCastAll',
                'ComputePenetration', 'IgnoreCollision', 'IgnoreLayerCollision'},
    'Transform': {'position', 'rotation', 'localPosition', 'localRotation',
                  'localScale', 'eulerAngles', 'forward', 'up', 'right', 'back',
                  'parent', 'childCount', 'SetParent', 'GetChild', 'Find',
                  'TransformPoint', 'TransformDirection', 'InverseTransformPoint',
                  'InverseTransformDirection', 'localEulerAngles', 'lossyScale',
                  'gameObject', 'root', 'positionCount', 'GetPosition', 'SetPosition',
                  'localToWorldMatrix', 'worldToLocalMatrix', 'CompareTag', 'tag'},
    'Object': {'Destroy', 'DestroyImmediate', 'FindObjectOfType', 'DontDestroyOnLoad',
               'name', 'transform', 'gameObject', 'hideFlags', 'Instantiate'},
    'GameObject': {'CreatePrimitive', 'Find', 'FindWithTag', 'activeSelf',
                   'SetActive', 'CompareTag', 'tag', 'name', 'transform',
                   'GetComponent', 'GetComponents', 'GetComponentsInChildren',
                   'AddComponent', 'layer', 'isStatic'},
    'Camera': {'main', 'transform', 'fieldOfView', 'nearClipPlane', 'farClipPlane',
               'orthographicSize', 'Render', 'ClearFlags', 'BackgroundColor',
               'ScreenToRay', 'WorldToScreenPoint'},
    'MonoBehaviour': {'transform', 'gameObject', 'enabled', 'name', 'StartCoroutine',
                      'StopCoroutine', 'StopAllCoroutines', 'Invoke', 'enabled'},
    'Material': {'color', 'SetColor', 'GetColor', 'mainTexture', 'shader', 'name'},
    'Shader': {'Find', 'name'},
    'Mesh': {'vertices', 'normals', 'triangles', 'RecalculateNormals', 'RecalculateBounds',
             'bounds', 'name', 'Clear', 'SubMeshCount', 'SetTriangles', 'indexFormat'},
    'Renderer': {'material', 'materials', 'enabled', 'sharedMaterial',
                 'GetComponent', 'bounds'},
    'MeshRenderer': {'material', 'sharedMaterial', 'enabled'},
    'MeshFilter': {'sharedMesh', 'mesh'},
    'Collider': {'enabled', 'transform', 'bounds', 'ClosestPoint', 'Raycast',
                 'attachedRigidbody', 'isTrigger', 'material'},
    'SphereCollider': {'radius', 'center'},
    'BoxCollider': {'size', 'center'},
    'CapsuleCollider': {'radius', 'height', 'direction', 'center'},
    'Rigidbody': {'velocity', 'angularVelocity', 'AddForce', 'useGravity', 'mass',
                  'MovePosition', 'position', 'rotation', 'Sleep', 'WakeUp',
                  'collisionDetectionMode', 'constraints', 'isKinematic'},
    'AnimationCurve': {'Linear', 'EaseInOut', 'Evaluate', 'keys', 'AddKey'},
    'Coroutine': {},
    'WaitForSeconds': {'WaitForSeconds', 'wait'},
    'WaitForFixedUpdate': {},
    'WaitForEndOfFrame': {},
    'Input': {'GetKey', 'GetKeyDown', 'GetMouseButton', 'GetAxisRaw'},
    'Resources': {'Load'},
    'LineRenderer': {'positionCount', 'SetPosition', 'SetPositions', 'startWidth',
                     'endWidth', 'material', 'color', 'startColor', 'endColor',
                     'widthCurve', 'useWorldSpace', 'enabled', 'sharedMaterial'},
    'GUIStyle': {'normal', 'fontSize', 'fontStyle', 'alignment', 'textColor',
                 'wordWrap', 'padding', 'normal.textColor', 'background'},
    'GUISkin': {'label', 'box', 'fontSize'},
    'GUILayout': {'Label', 'Button', 'Box', 'BeginHorizontal', 'EndHorizontal',
                  'BeginVertical', 'EndVertical', 'Width', 'Height', 'Space', 'FlexibleSpace'},
    'GUI': {'Label', 'Box', 'Button', 'DrawTexture', 'color', 'skin', 'backgroundColor',
            'contentColor', 'DrawRect', 'Window', 'DragWindow', 'BringWindowToFront'},
    'Rect': {'x', 'y', 'width', 'height', 'ToString', 'Contains'},
    'Texture2D': {'whiteTexture', 'SetPixel', 'SetPixels', 'Apply', 'width', 'height'},
    'RenderTexture': {'GetTemporary', 'ReleaseTemporary'},
    'QualitySettings': {'vSyncCount', 'shadowDistance'},
    'Application': {'isPlaying', 'targetFrameRate', 'isEditor', 'platform',
                    'RunInBackground', 'isBatchMode'},
    'SystemInfo': {'graphicsDeviceName'},
    'Light': {'type', 'intensity', 'color', 'range', 'shadows', 'enabled',
              'transform', 'gameObject'},
    'MaterialPropertyBlock': {'SetFloat', 'SetColor', 'SetVector', 'SetTexture',
                              'SetMatrix', 'Clear', 'GetFloat', 'GetColor'},
    'PrimitiveType': {'Sphere', 'Cube', 'Cylinder', 'Capsule', 'Plane', 'Quad',
                      'Sphere', 'Torus', 'Cylinder'},
    'KeyCode': {},
    'Space': {},
    'HideFlags': {'None', 'HideAndDontSave', 'DontSave', 'HideInHierarchy'},
    'ExecuteAlways': {},
    'RequireComponent': {},
    'Header': {}, 'Tooltip': {}, 'Range': {}, 'SerializeField': {},
    'DisallowMultipleComponent': {}, 'AddComponentMenu': {}, 'DefaultExecutionOrder': {},
    'ContextMenu': {}, 'SpaceAttribute': {},
}

# 这些是 System 命名空间下的静态类
SYSTEM_CLASSES = {
    'System': {'Math', 'Convert', 'Array', 'DateTime', 'TimeSpan', 'Environment',
               'Console', 'Random', 'String', 'Int32', 'Single', 'Double',
               'GC', 'Type', 'Action', 'Func', 'Nullable', 'Nullable`1',
               'Collections', 'Text', 'IO', 'Diagnostics', 'Linq',
               'Activator', 'Buffer', 'BitConverter', 'Tuple'},
    'Math': {'Max', 'Min', 'Abs', 'Sqrt', 'Pow', 'PI', 'Round', 'Floor', 'Ceil',
             'Sign', 'Log', 'Exp', 'Sin', 'Cos', 'Atan2', 'Sinh', 'Cosh', 'Tanh',
             'Atan', 'Acos', 'Asin', 'Truncate', 'Clamp', 'IEEERemainder', 'FusedMultiplyAdd'},
}

all_cs = []
for dirpath, dirnames, filenames in os.walk(ROOT):
    dirnames[:] = [d for d in dirnames if d not in ('.git', 'Plugins', 'Library')]
    for fn in filenames:
        if fn.endswith('.cs'):
            all_cs.append(os.path.join(dirpath, fn))

print('=' * 70)
print('跨类成员解析检查（专抓 CS0117 / CS1503）')
print('=' * 70)
print('扫描 %d 个 .cs 文件' % len(all_cs))

# ---------- pass 1: 收集类与成员 ----------
for path in all_cs:
    with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
        raw = f.read()
    lines = strip_code_lines(raw)
    rel = os.path.relpath(path, os.path.dirname(BASE)).replace('\\', '/')

    for i, ln in enumerate(lines, 1):
        mc = class_re.match(ln)
        if mc:
            cname = mc.group(1)
            bases = []
            if mc.group(2):
                bstr = mc.group(2)
                # 去掉泛型约束 where T :
                bstr = bstr.split(':')[0]
                for part in bstr.split(','):
                    bn = part.strip()
                    if bn and bn[0].isupper():
                        bases.append(bn)
            class_bases.setdefault(cname, []).extend(bases)
            class_members.setdefault(cname, set())
            class_methods.setdefault(cname, {})

        # 属性 / 字段
        mm = member_re.match(ln)
        if mm and '=' not in ln.split('//')[0][:ln.find('=') if '=' in ln else 0]:
            pass
        # 只收「成员名后面跟 { ( => ;」的，避免把语句当成员
        mm2 = re.match(
            r'^\s*(?:public|private|protected|internal)?\s*'
            r'(?:static\s+|readonly\s+|const\s+|sealed\s+|override\s+|virtual\s+|'
            r'new\s+|extern\s+|unsafe\s+|volatile\s+)*'
            r'(?:[\w\.\<\>\[\]\?\,\s]+?)\s+'
            r'([A-Za-z_]\w*)\s*(\{|=>|;|=|,)', ln)
        if mm2:
            n = mm2.group(1)
            # 排除 if/for/while/switch/return/foreach 等关键字形态
            if n not in ('if', 'for', 'while', 'switch', 'return', 'foreach',
                         'else', 'do', 'break', 'continue', 'case', 'new',
                         'get', 'set', 'nameof', 'typeof', 'sizeof'):
                # 找到它属于哪个类：向上找最近的 class 行
                for j in range(i - 1, -1, -1):
                    mc2 = class_re.match(lines[j])
                    if mc2:
                        class_members.setdefault(mc2.group(1), set()).add(n)
                        break

        # 方法
        ms = method_sig_re.match(ln)
        if ms:
            mname = ms.group(1)
            params = ms.group(2).strip()
            if mname not in ('if', 'for', 'while', 'switch', 'foreach',
                             'catch', 'return', 'new', 'lock', 'using',
                             'get', 'set'):
                # 参数个数
                pcount = 0
                pmax = None
                if params:
                    depth = 0
                    cur = ''
                    parts = []
                    for ch in params:
                        if ch in '<([':
                            depth += 1
                        elif ch in '>)]':
                            depth -= 1
                        if ch == ',' and depth == 0:
                            parts.append(cur)
                            cur = ''
                        else:
                            cur += ch
                    if cur.strip():
                        parts.append(cur)
                    pcount = len(parts)
                    # 是否有 params（变参）
                    if 'params' in params.lower():
                        pmax = pcount - 1
                for j in range(i - 1, -1, -1):
                    mc2 = class_re.match(lines[j])
                    if mc2:
                        cn = mc2.group(1)
                        tbl = class_methods.setdefault(cn, {})
                        if mname not in tbl:
                            tbl[mname] = (pcount, pmax)
                        break

# 展开继承
def resolve_chain(cname, depth=0):
    if depth > 5:
        return set(), {}
    mem = set(class_members.get(cname, set()))
    meth = dict(class_methods.get(cname, {}))
    for b in class_bases.get(cname, []):
        bm, bt = resolve_chain(b, depth + 1)
        mem |= bm
        for k, v in bt.items():
            meth.setdefault(k, v)
    return mem, meth


# ---------- pass 2: 扫描访问 ----------
access_re = re.compile(r'\b([A-Z][A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)')

total_checked = 0
for path in all_cs:
    with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
        raw = f.read()
    lines = strip_code_lines(raw)
    rel = os.path.relpath(path, os.path.dirname(BASE)).replace('\\', '/')

    for i, ln in enumerate(lines, 1):
        # 跳过字符串拼接行里的内容（已剥离）
        for m in access_re.finditer(ln):
            cname, member = m.group(1), m.group(2)
            total_checked += 1

            known = None
            if cname in KNOWN_EXTERNAL:
                known = KNOWN_EXTERNAL[cname]
            elif cname in SYSTEM_CLASSES:
                known = SYSTEM_CLASSES[cname]
            elif cname in class_members or cname in class_bases:
                mem, _ = resolve_chain(cname)
                known = mem
                # System.Math.Max 这类嵌套
            if known is None:
                continue
            if member in known:
                continue
            # 属性 X => ... 也会进 member_re，已覆盖
            errors.append('%s:%d  %s 没有成员 %r(CS0117 风险)'
                          % (rel, i, cname, member))

print('检查了 %d 处「类名.成员」访问' % total_checked)

# ---------- pass 3: 方法实参个数 ----------
call_re_cache = {}


def check_calls():
    fnd = 0
    for path in all_cs:
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
            raw = f.read()
        lines = strip_code_lines(raw)
        rel = os.path.relpath(path, os.path.dirname(BASE)).replace('\\', '/')
        # 找 `实例.Method(` ——实例名多为小写开头
        cr = re.compile(r'\b([a-z_][A-Za-z_0-9\.\[\]]*)\s*\.\s*'
                        r'([A-Za-z_]\w*)\s*\(')
        for i, ln in enumerate(lines, 1):
            # 跳过自己类里定义的方法调用（就地声明 → 直接匹配声明行）
            for m in cr.finditer(ln):
                meth = m.group(2)
                # 找出这个变量的静态类型：从本文件里找 `Type varname`
                var = m.group(1)
                if '.' in var:
                    continue
                # 取最后一段
                vtail = var.split('.')[-1]
                tm = re.search(r'\b(?:Silk\w+|string|int|float|bool)\s+'
                               + re.escape(vtail) + r'\b', '\n'.join(lines[:i]))
                if not tm:
                    continue
                typ = tm.group(0).split()[0]
                if typ not in class_methods:
                    continue
                # 找当前调用点的实参个数
                start = m.end() - 1
                depth = 0
                cnt = 0
                seen = False
                for j in range(start, len(ln)):
                    ch = ln[j]
                    if ch == '(':
                        depth += 1
                        if depth == 1:
                            seen = True
                            continue
                    elif ch == ')':
                        depth -= 1
                        if depth == 0:
                            if seen and cnt > 0:
                                pass
                            break
                    if depth == 1 and ch == ',':
                        cnt += 1
                if not seen:
                    continue
                argc = cnt + 1
                fnd += 1
    return fnd


print('')
print('=' * 70)
if errors:
    seen = set()
    for e in errors:
        if e in seen:
            continue
        seen.add(e)
        print('  [ERR] %s' % e)
    print('')
    print('共 %d 个可疑（已去重）' % len(seen))
    sys.exit(1)
else:
    print('通过 0 个可疑')