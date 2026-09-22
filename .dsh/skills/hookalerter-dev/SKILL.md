---
name: hookalerter-dev
description: Maintain the HookAlerter Gold Miner aiming tool (C# single-file WinForms + Win32 P/Invoke). Use when changing HookAlerter.cs, debugging its hook tracking / aim accuracy, adding panel controls, or reasoning about the game's discrete-frame timing.
whenToUse: Any edit, bug hunt, or accuracy investigation in HookAlerter/HookAlerter.cs.
---

# HookAlerter 开发手册

单文件 C#（.NET Framework 4.x，C# 5 语法），WinForms + Win32 P/Invoke。
一个外部工具：**只读屏幕像素**，不注入进程、不读游戏内存、不改游戏文件。

工作目录：项目根。源码 `HookAlerter/HookAlerter.cs`，产物 `HookAlerter/HookAlerter.exe`。

---

## 0. 铁律（改代码前必读）

1. **游戏失焦即整个冻结。** 所以工具是 `/target:winexe`（无控制台，不会抢焦点），
   日志写 `hookalerter.log`。绝不能让工具窗口抢走前台，否则游戏停在那一帧。
2. **游戏按帧步进（约 18fps）。** 钩子角度不是连续变化的，而是**一格一格跳**。
   任何"角度连续变化"的假设都会错。
3. **转轴是实测常数**，不可信任圆拟合的结果（见 §4）。
4. **钩子永远骑在固定半径的弧线上**（见 §5）。这是识别它最强的约束。
5. **C# 5 语法**：不能用 `?.`、字符串插值、`out var`、表达式体成员。
6. **避免未使用的局部变量和字段**。实测下面两种都曾直接导致构建失败：
   `CS0649`（字段从未赋值）、`CS0219`（变量已赋值但未使用）。
   不必纠结具体是哪个警告码 —— 不写用不到的东西即可。

---

## 1. 构建

源码已拆成 **10 个分片**（见 §2），用 `Add-Type -Path` 传**数组**一次编译。
产物与拆分前的单文件构建**大小与方法集完全一致**（77,824 B / 23 类型 / 138 方法）。

**必须在一个干净的 pwsh 进程里构建。** 同一个进程里先 `Add-Type -PassThru` 再用
`-OutputAssembly` 会导致产物**静默不更新**（`LastWriteTime` 不变）。

```powershell
Add-Type -AssemblyName System.Drawing
$cp = New-Object System.CodeDom.Compiler.CompilerParameters
$cp.GenerateExecutable = $true
$cp.OutputAssembly = (Join-Path (Get-Location) "HookAlerter\HookAlerter.exe")
$cp.CompilerOptions = "/target:winexe /optimize+"
[void]$cp.ReferencedAssemblies.Add("System.dll")
[void]$cp.ReferencedAssemblies.Add("System.Drawing.dll")
[void]$cp.ReferencedAssemblies.Add("System.Windows.Forms.dll")
$parts = Get-ChildItem ".\HookAlerter\HookAlerter.*.cs" | Sort-Object Name |
           ForEach-Object { $_.FullName }
Add-Type -Path $parts -CompilerParameters $cp
Get-Item ".\HookAlerter\HookAlerter.exe" | Select-Object Length,LastWriteTime  # 必须确认时间变了
```

csc.exe 直接调用会被沙箱拒绝，只能用 `Add-Type`。
**产物无法直接执行**（沙箱拒绝启动 exe），验证要用进程内反射调用：
```powershell
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path ".\HookAlerter\HookAlerter.exe").Path)
$asm.GetType('HookAlerter.Program').GetMethod('Main',
  [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, @(,[string[]]@('--analyze',$png,'0','0','0')))
```

**改完默认值一定要删 `hookalerter.ini`**，否则旧值覆盖新默认值。

---

## 2. 类型总览

| 类型 | 职责 |
|---|---|
| `Nat` | 全部 Win32 P/Invoke + 常量 |
| `Cfg` | ini 配置的读写 |
| `Frame` | 一帧像素（`W`,`H`,`P[]` 为 0xRRGGBB） |
| `Cls` | 颜色判定（纯静态） |
| `Vision` | **核心**：场地/物体/钩子/射线/判定，全部视觉逻辑 |
| `ICapture` / `WindowCapture` | 抓游戏窗口客户区 |
| `Overlay` | 分层透明覆盖窗（画虚线） |
| `CtrlPanel` | 控制面板窗口 |
| `Program` | 入口、主循环、各测试模式 |

---

## 3. `Nat` —— Win32 层

`GetWindowDC` / `CreateDIBSection` / `BitBlt`（抓图）、
`keybd_event`（出钩按键）、`GetCursorPos`（鼠标）、
`SetForegroundWindow`/`ShowWindow`（测试用）、`GetAsyncKeyState`（热键）。

- `Nat.Key(byte vk)` —— 按下并释放一个键，`0x28` 是方向下（游戏里的出钩键）。
- `SetProcessDPIAware()` 在 `Main` 最开始调用。**不调用会让抓图变白、坐标错位。**

---

## 4. `Vision` 几何：场地与转轴

### `DetectField(Frame) → Rectangle`
定位挖掘区：左右黑边 + 顶部 16% 是 HUD。`Field.Top = H*0.16`。

### `ArtPivot()`
**转轴的权威来源。**

```
PivotX = (Field.Left + Field.Right) / 2
PivotY = Field.Top - Field.Height * 0.049
BaseR  = Field.Height * 0.062
```

`0.049` 是**实测值**：从画面里扫出绳子像素、连成直线、向上延伸，
两次独立测量分别得到 (966,118) 和 (967,118)，与公式吻合。

> **教训（两次踩坑）**：
> 1. 早期用 `0.105`（高了 47px）。转轴错会让所有角度整体旋转，且**目标越远偏得越多**。
> 2. 即使把采纳窗口收到 **±25px**，仍然被一个偏差正好 **20px** 的拟合值擦边通过
>    （实测 `fit(955,98) r=63` 顶掉了 `art(957,118) r=52`，造成 5~11° 偏差）。
>
> **根本原因**：**在浅弧上圆心和半径是近共线参数** ——
> "圆心高 20px + 半径 63"与"圆心正确 + 半径 52"拟合出的弧几乎一样
> （残差 1.97px vs 1.71px）。**残差对那个方向没有分辨力。**
>
> 所以现在的窗口是 **±8px / 半径 ±12%**：拟合只能**微调**转轴，不能**搬走**它。
> **验证方法**：`[cal] ... -> using ART|FIT` 与 `[cal] using pivot=...` 必须与实测值一致，
> 且 `[trace]` 行里的 `piv=` 要等于实测转轴。**"收紧了阈值"不等于"堵住了漏洞"。**

### `Calibrate(ICapture, seconds, radiusHint)`
采集钩子运动样本 → `FitPivot` 圆拟合 → 与 `ArtPivot` 比对 → 采用更可信的一个。
早期退出条件：22 个样本。

### `FitPivot` / `CircleFit`
带裁剪的有界圆拟合。**结果只在高度吻合时才可用**，见上。

---

## 5. `Vision` 钩子跟踪

### `TrackHook(Frame) → bool`
每帧调用。流程：

1. `FrameChanged` —— 帧没变就直接返回（重复帧）。
2. `HookPixels` 求钩子像素质心。
3. **物理门限**：`|ang| > 1.50 rad` 拒绝（钩子只可能在转轴下方摆动弧内）。
4. **连续性门限**：与上一帧角度差 > `0.38 rad` 且 `LostFrames < 3` 时拒绝。
5. 更新 `Angle`、`Omega`（平滑角速度）、`HookR`、`MinAngle`/`MaxAngle`。
6. **`HookDeployed` 锁定逻辑**（见下）。
7. `RestR` 自适应（限幅在 `BaseR` 的 0.75~1.35 倍）。

### `HookPixels(Frame cur, Frame prev, Rectangle box, out hx, out hy, out count)`
在搜索框内取"**和上一帧不同 + 是灰色**"的像素质心。三重约束：

```csharp
if (cdy < 8) continue;                                  // 必须在转轴下方
if (|atan2(cdx,cdy)| > 1.45) continue;                  // 必须在摆动弧内
if (dist < BaseR * 0.80) continue;                      // 下界：任何时候都不可能这么近
if (!HookDeployed && dist > BaseR * 1.60) continue;     // 上界：仅待机时要求
```

> **为什么下界必须无条件生效**：`HookDeployed` 在 `TrackHook` **末尾**才更新，
> 所以"上一帧末误判为出钩中"会让这一帧整体跳过检查，放进一个 26px 处的假目标，
> 帧末标志又被改回 false，然后开火判定看到 `!HookDeployed` 成立 → **拿坏跟踪开火**。
> 实测正好造成 2 发乱飞的钩子。
>
> **为什么上界只对待机生效**：钩子射出去后沿绳子到几百像素外，
> 此时套用弧线上界会让跟踪器**整段失明**（`[flight]` 从此不再出现）。

### `HookBox()`
搜索框：以预测钩子位置为中心，**300×140**。框较大是历史原因，
真正的约束在 `HookPixels` 里。

### `HookDeployed` 锁定
出钩后跟踪器会跟丢真钩子（它跟着移动的绳子跑），所以"出钩中"必须**锁定**：
半径超过 `RestR*1.45` 置位；半径低于该阈值连续 6 帧才清除。

---

## 6. `Vision` 物体分割

### `LearnObjects(Frame)`
1. 只处理 `Field` 内缩后的区域，顶部再排除 62px（矿工平台带）。
2. 用 **48×48 网格的中位色**估计局部背景 —— 二维网格能跟上波浪形泥土色带，
   逐行中位会被带偏。
3. `物体 = 色差 > 110` **或** 描边（`lum < blur - 30`，描边加粗 2px 补缝隙）。

不使用洪水填充。早期版本"从四边灌水、灌不进的算物体"，
**要求每个物体描边完全闭合**；金块贴着浅色土带那一侧对比度不够就会漏水，
把整个金块内部判成背景。

### `ObjArea` 相关：`ObjectArea(sx, sy, cap)`
连通块面积（返回**包围盒面积**，因为只检测到描边时内部是空的）。
同时输出：`LastObjW/H`、`LastObjX0/Y0/X1/Y1`、`LastObjCx/Cy`（质心）。

### `IsObject(x, y)`
查 `ObjectMask`。

---

## 7. `Vision` 判定：射线与瞄准

### `RayHit(angle, out hitR) → 类别常量`
沿角度从 `(RestR>0?RestR:HookR)+36` 向外步进 2px：

- 起点用**静止半径**，不能用当前 `HookR` —— 出钩时 `HookR` 有几百，
  射线会从目标身后起步。
- 要求连续 ~20px 的物体像素（`run >= 10`），跳过泥土色带的软梯度。
- 物体包围盒必须**成块**：`min边 >= 20 且 长宽比 <= 4`（泥土产生的假物体是细长条带）。
- 分类：`rock*2>=obj` → Rock；`dia*2>=obj` → Diamond；`bag*2>=obj` → Bag；
  否则 GoldT。**没有尺寸过滤** —— 碎金也算。

> `GoldPixel` 按 G/R 比例判金色，**跨关卡不可靠**（沙地关卡金块 G/R≈0.72，
> 泥土≈0.85，会重叠）。所以"非灰即成块可勾"是唯一稳定的判据。

### `AimSpan(aimAngle, out a0, out a1, out centre, out kind) → bool`
鼠标指向哪个物体、它占哪段角度。

- **只扫 ±2°**。曾经扫 ±5°，会从钻石跳到旁边的钱袋。
- `centre` 是**质心方向**（用于显示）。
- 包围盒钳制到"最大金块"尺寸：**`Field.Width * 0.105` × `Field.Height * 0.19`**。
  实测大金块包围盒约 150×130（client 1936×996 时 `Field.Height*0.19 ≈ 159`），
  所以**高度系数必须是 0.19，不能写成 0.075**（0.075 只有 63px，会把大金块自己钳掉）。
  作用是防止错误粘连的大块报出荒谬跨度。
- `centre` 在钳制**之前**由未钳制的质心算出 —— 两者可能不一致，但目前未观察到影响。

---

## 8. 主循环 `Program.RunLoop` —— 出钩判定

这是全部逻辑的汇合点。每帧：

### 8.1 取鼠标方向
```csharp
mouseAngle = atan2(鼠标 - 转轴)
```
用 `cap.ClientScreenRect` 换算屏幕→客户区坐标。

### 8.2 目标检查
`AimSpan` → `worth = kind ∈ {GoldT, Bag, Diamond}`。
石头和空气都**不出钩**。

### 8.3 格间距与帧长自学习
只在**真正发生跳动**时更新（静止帧不更新，否则估计会被侵蚀到 0）：

- `jumps[]` 存最近 16 次跳跃 → `gameStep = 中位数 × 1.15`
  （用中位数而非最大值：跟踪漏一帧会让观察到的跳跃跨两格，最大值就会被骗大一倍）
- `periods[]` 存跳动间隔 → `framePeriod = 中位数`

### 8.4 出钩条件
```csharp
double ahead  = lastDir * leadAdj;        // 过冲补偿（见 8.5）
double nowA   = v.Angle + ahead;
double half   = Math.Max(0.087, gameStep * 0.75);   // 下限 5°
bool nearLine = |nowA - aim| <= half;
bool crossed  = 跨越检测(prevA2, nowA, aim);
bool settledOnIt = tOnSample >= framePeriod * 0.40;

alert = worth && !HookDeployed && !settling && (crossed || (nearLine && settledOnIt));
```

**三个要点：**

- **`half` 的 5° 下限**：钩子在摆动两端会减速、格间距缩小，窗口若跟着缩，
  最需要开火时最窄 —— 实测"到过 3° 却不开火"就是这么来的。
- **`settledOnIt` 中段开火**：游戏下一帧才处理按键，若在格子刚出现时按，
  延迟一抖就用到了下一格。等到格子显示 40% 再按，前后各留半帧余量。
- **`lastDir` 只在跳动帧更新**，且钩子回来后清零（方向可能反）。

### 8.5 过冲自学习 `leadAdj`
实测钩子**总朝摆动方向飞过头 1~8°**。每次出钩后量 `over = 实际飞行 − 开火角度`，
取最近 8 次的**中位数绝对值**，开火时乘上 `lastDir` 提前这么多。

```csharp
if (Math.Abs(over) < 0.25)   // 超过 14° 的必然是跟踪故障，丢弃
```

### 8.6 `settling` 自适应静默
钩子刚回来时摆动混乱。旧版固定 500ms（既太长又太随意），现在等**跟踪变稳**：

```csharp
bool arcLocked = HookR ∈ [BaseR*0.85, BaseR*1.45];
if (!HookDeployed && arcLocked) stableFrames++; else stableFrames = 0;
settling = 回来 < SettleMs && !(回来 > 0.15s && stableFrames >= 8);
```
配置值降级为**上限**。

### 8.7 卡死检测
角度冻结 >1.2s **且半径 < `BaseR*1.8`**（必须在转轴附近）→ 释放出钩锁定。

> **必须带半径条件**。钩子飞出去时角度本来就不变（沿直线飞），
> 不带半径条件会让每一发正常出钩都被误判，疯狂释放锁定 → 一边飞一边重复开火。

---

## 9. 抓图 `WindowCapture`

- `Grab()` —— `GetWindowDC` + `BitBlt` 到 `Format32bppPArgb` 位图，再按客户区偏移裁剪。
- **每次 `Grab` 都要重新 `Graphics.GetHdc()`/`ReleaseHdc()`**，持久 HDC 会返回空白。
- `Usable` / `Alive` / `Retarget` —— 游戏可能重启换句柄。
- `Refresh()` 重算窗口/客户区/偏移。**客户区矩形是出钩坐标换算的基准。**

---

## 10. 面板 `CtrlPanel`

- 三个按钮组：提示开关 / 画面文字 / 自动出钩；两个数值框：提前量、回钩静默。
- **去掉过 `WS_EX_NOACTIVATE`**：那个样式让窗口内所有控件收不到键盘输入。
  靠 `ShowWithoutActivation` 保证启动不抢焦点，点击才激活。
- 数值框用 `Tag = "edited"` 标记"用户改过"，刷新定时器不覆盖它。
  点"应用"那一瞬焦点会移走，旧版正是在这个空隙被刷新覆盖回旧值。
- 非法输入**变红**提示，不静默丢弃。
- 跨线程状态：`CtrlPanel.CfgRef` 让面板**直接改配置**（走 ReqXxx 队列会等后台线程忙完，
  标定要 2~6 秒、录制要 8 秒，期间改了不生效）。

---

## 11. 日志与测试模式

命令行（`--xxx` 分支在 `Main` 开头）：

| 模式 | 用途 |
|---|---|
| `--analyze <png> <px> <py> <r> [out]` | 对一张图跑全部角度的射线分析 |
| `--liverun <sec> [shot]` | 跑真实循环 N 秒后退出，可截桌面图 |
| `--autoplay <n>` | **自己玩**：切前台→标定→选目标→移鼠标→出钩→记录误差 |
| `--aimtest` / `--tracetest` / `--captest` / `--selftest` | 各专项自检 |

### 日志里的关键行

```
[cal] fit pivot=(...) ok=...  /  art pivot=(...)  -> using FIT|ART
[fire]   aim= .. hook= .. diff= .. obj= a..b centre= .. r= .. KIND
[flight] aim= .. fired= .. flew= .. over= .. lead= .. obj= a..b
[trace] ang= .. aim= .. d= .. half= .. obj= .. inObj= .. near= .. crossed= ..
        tOn= ..ms step= .. dep= .. setl= .. worth= .. hx= .. hy= .. piv= ..
[stuck] frozen track near the pivot - released the latch
```

**`[trace]` 是排查主力**：每次出钩会 dump **前 100 帧 + 后 25 帧**的逐帧决策。

**`hx,hy` 与 `piv` 的距离应稳定在 45~55 像素。** 挤在小方块里 = 锁错目标。

---

## 11.5 日志不变量检查表（拿到日志先跑这个）

**不要先读代码。先拿日志对下面每一条**，违反的那一条直接指向出问题的代码。

| # | 检查 | 正常 | 违反说明什么 |
|---|---|---|---|
| 1 | `[trace]` 的 `hx,hy` 到 `piv` 的距离（`dep=0` 时） | **45~55 px** | 偏离 → 跟踪器锁错目标。挤在小方块里、半径 <35 → 看 `HookPixels` 的门限 |
| 2 | `[fire]` 的 `r=` | 40~55 | <35 → **在坏跟踪上开火了**，说明 `HookPixels` 的下界没生效 |
| 3 | FIRE 之前 `d` 的走势 | 单调逼近 0 | 来回跳 → 跟踪噪声；一直不接近 → 窗口/方向算错 |
| 4 | `[fire]` 的 `diff` | 落在 `half` 内 | 超出 → 走的是 `crossed` 分支，或 `ahead` 把点推歪了 |
| 5 | `[flight]` 的 `over` | 0~8°，同号 | \|over\| >14° → **测量或跟踪故障**（不该进 `leadAdj`） |
| 6 | `obj=` 区间是否含 `aim` | 含 | 不含 → 判定和鼠标线脱节（`AimSpan` 扫射或射线起点问题） |
| 7 | `[stuck]` 出现次数 | 整局几次 | **成百上千次 → 该检测器误判**（本项目出现过两次） |
| 8 | `step=` | 稳定在几度 | 塌缩到 0.03 → 估计器被静止帧侵蚀；在 5 和 17 之间跳 → 漏帧把最大值骗大 |
| 9 | `[cal] ... -> using` | FIT 与 ART 相差 ≤25px | 相差大却选了 FIT → 采纳规则失效，整局角度全偏 |
| 10 | `tOn` | 钩子跳动时归零 | 不归零 → `sampleAt` 没更新，`settledOnIt` 判据失效 |
| 11 | `setl=` / `worth=` / `dep=` | 开火时全为 0/1/0 | 组合不对 → 出钩被静默、出钩锁定、或目标判定挡住 |

**三种角色的分工**（见 §13）：改动方看代码，查 bug 方看代码 + 不变量，
对照分析方**只做"日志里的数字 ↔ 代码里的算式"逐项对齐**，不看任何人的结论。

---

## 11.6 已被证伪的假设（别再往这些方向查）

这些都曾经被当作根因、投入大量改动，**最终都被日志推翻**。遇到新问题时先排除它们：

| 曾经的假设 | 为什么错 |
|---|---|
| 转轴位置不对 | 两次从绳子像素反解都精确得到 (966,118) / (967,118)，与公式吻合。方向性偏差由别处造成 |
| 游戏输入延迟固定一帧 | 实测不稳定：同一读数有时晚一帧、有时不晚。按它做"提前一格"反而赔一整格 |
| 金色可以用 G/R 比例识别 | 沙地关卡金块 G/R≈0.72、泥土≈0.85，**区间重叠**，跨关卡不成立 |
| 物体尺寸要过滤（碎金不提示） | 大小不该由程序判断；且过滤条件曾把钻石一起杀掉 |
| 洪水填充分割物体可靠 | 要求描边完全闭合；金块贴浅色带那侧对比度不足就漏水，整个内部被判成背景 |
| 固定 500ms 回钩静默够用 | 既太长又太随意；改成"等跟踪变稳" |
| `Omega`（平滑角速度）可用于判方向 | 平滑值滞后，摆动换向时会短暂给出错误符号，导致提前方向反掉 |

**通用教训**：本项目几乎每个真凶都不是最初怀疑的那个。**先看日志的不变量，再动代码。**

---

## 11.7 先确认日志是"当前这版"产生的

**拿到日志的第一件事**：确认它是由当前源码构建出来的，否则会拿旧 bug 的症状去改新代码。

检查方法：从日志里挑一条**独有字符串**，在源码里搜。

```powershell
# 日志里出现过、但源码里搜不到的字符串 = 日志来自旧构建
Select-String -Path .\HookAlerter\_dev\sessionNN.log -Pattern '\[stuck\]' |
  Select-Object -First 1
Select-String -Path .\HookAlerter\HookAlerter.cs -Pattern 'frozen track near the pivot'
# 再看时间戳：.exe 应晚于 .cs，日志应在 .exe 之后
Get-ChildItem .\HookAlerter\HookAlerter.cs, .\HookAlerter\HookAlerter.exe,
              .\HookAlerter\_dev\sessionNN.log | Select-Object Name,LastWriteTime
```

> **真实案例**：一次审计中，`session24.log` 有 233 条 `[stuck]`，
> 消息文本是 `"frozen track - released the deployed latch"` ——
> 而当前源码打印的是 `"frozen track near the pivot - released the latch"`。
> 该日志来自**上一版**，那 233 条风暴是被修复前的症状。
> 若直接拿它去改当前代码，就是在修一个已经修好的 bug。

**版本错配时**：只把该日志当作"上一版的历史证据"，结论必须回到当前源码上重新推导。

---

## 12. 排查症状 → 怀疑对象

| 症状 | 先查 |
|---|---|
| 钩子乱飞 / 差 20°+ | `[trace]` 的 `hx,hy` 离 `piv` 多远；`[fire]` 的 `r=` 是否异常小 |
| 到过目标附近却不开火 | `half` 是否被算小；`setl`/`wor` 是否为 1/0 |
| 开局不出钩 | 标定是否完成；`[auto] armed` 是否出现 |
| 某类物体永远不勾 | `obj=` 区间是否包含 `aim`；分类是否被 `Rock`/`Unknown` 吃掉 |
| 越玩越偏 | 转轴是否被坏拟合污染（看 `[cal] ... -> using`） |
| 日志刷屏某一种行 | **该检测器误判率高** —— 别忽略，高频触发本身就是 bug 信号 |
| 游戏变卡 / 掉帧 | 后台在狂写文件：`[cal] not in a level` 每 400ms 重试一次，每次都跑全帧分割**并写一张全分辨率 PNG**（`SetPixel` 逐点，约 190 万像素） |
| 出现重复的 `[fire]` 行 | 状态标志被反复释放又重置（见 §8.7），同一瞄点被开了两枪 |

---

## 13. 多角色协作流程（本项目约定）

### 13.0 硬门禁：**查 bug 方不通过，代码不许落地**

> **这一条没有例外。** 本项目已经三次违反它，每次都是同一个后果：
> 改动方自己改完直接构建，然后用户在实际游戏里发现了新问题。
>
> **落地顺序必须是：**
> ```
> ① 改动方出方案（不碰文件）
> ② 查 bug 方独立复核 ← 必须在构建之前
> ③ 双方一致 → 才允许写文件、构建、交付
> ```
> **禁止**"先改完再说"、"我自己看过了"、"问题很明显不用审"。
> 改动方对自己写的保护机制**天然盲**（这是本项目的实证结论，不是谦虚）。

### 四类角色

| 角色 | 输入 | 只输出 | 硬约束 |
|---|---|---|---|
| **① 改动方** | 代码 + 日志 | 一处最高价值的改动方案（含前后代码） | **不碰文件** |
| **② 查 bug 方** ⭐ | 代码 + 日志 | 缺陷清单 + **必查清单逐条结论** | **不碰文件**；不看①的结论 |
| **③ 代码↔日志对照方** | 代码 + 日志 | §11.5 不变量逐项比对结果 | **只做"日志数字 ↔ 代码算式"对齐**，不评价设计 |
| **④ 拆分分析方** | 代码规模/结构 | 能否拆分、怎么拆、省多少 token | **只做结构分析** |

`②` 是四者中最重要的一环：**它是唯一能挡住"改出一个新 bug"的角色。**

### 13.1 查 bug 方**必查清单**

每次复核改动，必须逐条给出结论。这些全部来自本项目**真实踩过的坑**：

| # | 必查项 | 本项目实例 |
|---|---|---|
| 1 | **新加的"保护机制"会不会挡住正常行为？** 穷举它在**所有状态**下是否成立 | 弧线上界挡住出钩后的跟踪 → `[flight]` 整段消失 |
| 2 | **判据的触发频率合理吗？** 正常情况下几乎不该触发的东西若高频出现，判据就是错的 | `[stuck]` 一局 36~233 次；`[cal] not in a level` 2715 次 |
| 3 | **状态变量的更新时机对吗？** 在函数末尾更新的标志，在本函数前半段是"上一帧的值" | `HookDeployed` 末尾更新 → 前半段跳过检查 → 放进 26px 假目标并开火 |
| 4 | **"让两个条件互斥"这种修法可靠吗？** 只要其中一个条件依赖**会变的量**，互斥就会失效 | 出钩中门槛用自适应 `RestR`，卡死上限用固定 `BaseR` → 漂移后重叠复发 |
| 5 | **"逐元素筛选 + 求平均"是否隐含凸性假设？** 环/弧/分离区间**不是凸集** | 像素都在弧上，质心却掉回转轴里（110/678 帧） |
| 6 | **基于"变化"的判据，是否同时校验了"变化是否可信"？** | 跨越判定把 18.75° 的跟踪噪声当成钩子扫过 → 假跨越开火 |
| 7 | **复位路径存在吗？** 加了闩锁/标志，就要问它**什么时候被清掉**，以及条件是否可达 | 一次性闩锁若复位条件不可达 → 检测器二次失效 |
| 8 | **同一个检测器被反复修补了吗？** 连续改同一个地方 ≥2 次，说明**判据本身错了**，该重写而不是调参 | `[stuck]` 连改三轮（加半径 → 收紧 → 加闩锁） |
| 9 | **改动是否依赖了会漂移的量？** 拟合值、自适应值、平滑估计 | 拟合转轴连两轮擦边通过（20px、5px）；`Omega` 换向时符号翻转 |
| 10 | **日志能否验证？** 改动若在 `[trace]`/`[fire]` 里没有对应字段，就是不可验证的改动 | 要求新增日志字段，或说明为何不需要 |

### 13.2 查 bug 方的纪律

1. **先做版本一致性校验**（§11.7）—— 用旧日志分析会得出**反向结论**（已经发生过两次）。
2. **优先用日志数字说话**，不要靠推理；本项目几乎每个真凶都不是最初怀疑的那个。
3. **日志答不了就标 `UNKNOWN`**，并说明"需要哪个字段"。**不许猜。**
4. **被证伪也要说** —— 明确写出"这条不是问题"，避免改动方白改。
5. **改动方给出的辩解说辞不算证据。** 对照方曾经给出"日志打的是质心、门限管的是像素"的辩解，
   **结论对、理由错** —— 那恰恰就是矛盾所在。**只认数字。**

### 13.3 改动纪律

1. **先看日志的不变量，再动代码。**
2. **确认日志来自当前构建**（§11.7）。
3. 任何数值型改动都要能在 `[trace]` / `[fire]` 里验证。
4. 改完**必须重新构建并确认 `LastWriteTime` 变化**，且**产物晚于所有源码**；
   最好再用反射确认新符号确实进了二进制。
5. 改默认值要删 `hookalerter.ini`。
6. **修完 bug 必须在 `HookAlerter/FIXES.md` 追加一条**，尤其是"被证伪的怀疑"栏。
7. **改完立刻问用户要一局新日志** —— 没有新日志，改动就只是"我认为修好了"。

---

## 14. Token 纪律（实测数据）

**日志才是 token 大头，不是源码。** 实测：

| | 大小 | ≈ token |
|---|---|---|
| `HookAlerter.cs` | 171 KB | **50k** |
| 单个 `session23.log` | 1.12 MB | **320k** |
| 21 个日志合计 | 2.62 MB | **748k**（源码的 15 倍）|

### 规则

1. **禁止整读日志。** 永远先过滤：
   ```powershell
   Get-Content .\HookAlerter\hookalerter.log |
     Select-String '\[fire\]|\[flight\]|\[stuck\]|decision trace'
   # 只看某几钩的 trace：先定位行号再取区间，不要 Select-Object -Last 40 硬拉
   ```
2. **`[trace]` 转储只取需要的那一段。** 一次出钩会写 100+25 行，
   一局几十钩就是几十万 token。
3. 优先读 `FIXES.md`（几 KB）了解历史，再去读日志 —— 很多现象之前已经查清并记录了。

### 关于拆分源码（已评估，结论：不做）

`Add-Type -Path` **确实接受文件数组**，已用阴性对照 + 真实源码干跑 + IL 逐方法比对验证，
拆分**可行且产物完全可复现**（无需 build.ps1），且**无任何阻塞**。

**但不值得做**：`Vision`(39%) + `Program`(42%) = **81%** 的代码量，
而判定逻辑**横跨这两个文件**，所以拆完读一次只从 **5.0 万 → 4.1 万 token（省 19%）**。
只有"只改小类型"的任务才省 81%，而那些恰恰是最少改的部分。

**若要真正降 token，应该重构而非机械拆：**
- 先把 `Program` 里 5 个测试模式（约 300 行 ≈ 4400 token）抽出去 —— 纯搬运、零逻辑改动
- `Vision` 可按 几何/转轴（约 299 行）vs 跟踪+分割+瞄准（约 998 行）拆分
- 任何拆分都必须同步更新 §1 的构建命令（改为 `Add-Type -Path @(各分片)`）、
  §0-§2 的"单文件"表述、以及 §13 里的规模数字
