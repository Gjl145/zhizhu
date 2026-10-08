# zhizhu 项目技能与流程（每次任务必读）

> ★ 用户要求（2026-10-08）：
> 「现在包括之后的所有改动之后加自检，检查是否翻过之前犯过的错误，
>   这个每一次任务都要做」

## ★★ 每次改动后必跑的两个自检

```bash
cd C:/Users/1/Desktop/wdyouxi/zhanchang/zhizhu

# 1) 结构自检（39 项）
python .workbuddy/tools/selfcheck.py

# 2) ★ 历史错误自检（本次新增，每次必跑）
python .workbuddy/tools/check_regressions.py
```

★ **两个都要跑**。`selfcheck.py` 管代码结构，`check_regressions.py`
管「有没有重犯之前犯过的错误」。

---

## check_regressions.py 检查什么

| 检查 | 对应历史错误 |
|---|---|
| 成员被跨类引用 | CS0103（已栽 6 次）|
| 局部 const 被其他方法引用 | `stairW` 被 `BuiltMessage` 引用 |
| `RoomLength` 必须是公式 | 曾硬编码 `(4.5*5.0)`，改 `RoomSize` 而房间不变 |
| `StepRise` 必须是公式 | 曾硬编码 `0.16`，改层高后楼梯到不了二层 |
| 起点高度必须是公式 | 曾硬编码 `-29.5`，球缩小后悬空 2 格 |
| 相机/球径比例 | 球缩小后相机没跟着缩，球像一粒米 |
| 住宅相机距离 ≤ 12 格 | 曾是 20 格（房间半宽只有 17.5）|
| 门洞宽度 ≤ 1.0 米 | 曾因球径过大加宽到 1.10（现已恢复国标）|
| selfcheck 自身的弱检查 | 提醒「只数标识符次数」「截取范围过小」|

**变异测试**：5 条全部验证有效（改成字面量/旧值 → 逐条被抓到）。

---

## ★ 已放弃的检查（不要再尝试）

### 1. 通用 CS0103 自动检测（跨类成员引用）

**已放弃**，原因记录在 `check_regressions.py` 的 `check_member_ownership()`
的 docstring 里，以及 `selfcheck.py` 第 22 项。

★ 尝试过 6 版全部因误报失败（226 条噪音）。
★ **可靠替代：看 Unity Console 的编译报错**——CS0103/CS1061
会给出精确行号。本项目 6 次 CS0103 **全部是编译器发现的**，不是脚本。

★ C# 的作用域需要真正的语法分析（Roslyn），文本正则做不到。

---

## 新增代码时的自检清单

```bash
# 1. 括号配平（最快的语法检查）
python -c "
import re
s=open('Assets/Silk/SilkBuilder.cs',encoding='utf-8').read()
t=re.sub(r'//[^\n]*','',s); t=re.sub(r'/\*.*?\*/','',t,flags=re.S)
print('{ %d } %d' % (t.count('{'),t.count('}')))
print('( %d ) %d' % (t.count('('),t.count(')')))
"

# 2. 确认新成员在正确的类里（同文件有多类时必查）
python - <<'EOF'
import re
lines = open('Assets/Silk/SilkBuilder.cs', encoding='utf-8').read().split('\n')
classes = [(i+1, re.match(r'\s*(?:public |internal |static |partial )*class (\w+)',
          ln.strip()).group(1))
           for i, ln in enumerate(lines)
           if re.match(r'\s*(?:public |internal |static |partial )*class (\w+)', ln.strip())]
print("类边界:", classes)
EOF

# 3. 两个自检
python .workbuddy/tools/selfcheck.py
python .workbuddy/tools/check_regressions.py
```

★ **第 2 步特别重要**：`SilkBuilder.cs` 里已有 16 个类，
按行号猜插入点极易插错类（CS0103 第 6 次就是这样错的）。

---

## 用户偏好（沟通相关）

★ **报 Unity 选项时必须用「中文（English）」双语**，例如：
- 层级（Hierarchy）
- 检查器（Inspector）
- 变换（Transform）/ 位置（Position）/ 旋转（Rotation）/ 缩放（Scale）
- 游戏对象（GameObject）
- 播放（Play）

★ 用户装了中文包，看英文界面会找不到对应项。

---

##★ 不能做的事

| 不能 | 为什么 |
|---|---|
| 用脚本批量替换代码/注释 | ★ 今日两次误删（一次 5500 行、一次 200 行）|
| 凭印象报菜单位置 | ★ 今日给错 3 次（ProBuilder 6.x 版本号、New Shape 菜单、Plane 缩放轴）|
| 不查源码就报 API | 报菜单位置前先 grep 包源码确认 |
| 新增按键 | 用户硬性约束（8 键定稿）|
| 擅自改场景文件 | 用户要自己搭场景 |

---

## 工具速查

| 脚本 | 用途 |
|---|---|
| `selfcheck.py` | 39 项结构自检（含 CS0103 历史清单、参数联动）|
| `check_regressions.py` | ★ 历史错误自检（本次新增）|
| `check_stage.py` | 关卡可通关性核验（解析代码里的 `Plat()` 调用）|
| `check_movespeed.py` | 手感量级检查 |

★ `check_stage.py` 只认**代码里写死**的平台，读不到手工搭的场景。
用户自己搭场景后它无效——这也是 `freeBuildMode` 存在的原因。

---

## 当前状态快照（2026-10-08）

| 项 | 值 |
|---|---|
| Unity | 2022.3.62f3c1，Built-in 管线（非 URP）|
| 球直径 | 2.5 格 = 0.5 米（占房间 7.1%）|
| 房间 | 7.0 米见方，净高 2.90 米 |
| 跳跃最高 / 全速跨度 | 5.2 格 / 16.3 格 |
| 自检项数 | 39（selfcheck）+ 5 类（check_regressions）|
| 提交数 | 约 130 |
