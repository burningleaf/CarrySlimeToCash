# Unity 侧搭建与场景连线手册

配套文档：`设计文档_带呆呆史莱姆越障换钱.md`（玩法与架构）、`Assets/_Project/Scripts/**`（22 个脚本）。

---

## 1. 创建工程

1. Unity Hub → New project → **Universal 2D / 2D (URP)** → Editor 版本选 **Unity 2022.3 LTS**。
2. 工程名随意（例：`SlimeDemo`），创建完成后关闭 Unity。
3. 把本目录的 `Assets/_Project` 整个文件夹复制到新工程的 `Assets/` 下（得到 `Assets/_Project/Scripts/...`）。
4. 重新打开 Unity，等待编译。**首次编译不应有报错**；若有，见第 12 节。

脚本产出的目录：

```
Assets/_Project/Scripts/
  Managers/  GameManager.cs  LevelManager.cs
  Player/    PlayerController.cs  PlayerInventory.cs  PlayerGrab.cs
  Slime/     SlimeController.cs  SlimePathFollow.cs  WaypointMarker.cs
  Items/     Coin.cs  SlimeOrb.cs
  Level/     Hazard.cs  Goal.cs  PressurePlate.cs  Checkpoint.cs  Enemy.cs  MovingPlatform.cs
  UI/        UIManager.cs  InventoryUI.cs  LevelResult.cs  PauseMenu.cs  MainMenu.cs  LevelSelectUI.cs
```

---

## 2. Tags and Layers（Project Settings → Tags and Layers）

### 2.1 Layers（User Layer 6 起，按顺序填）

| 索引 | 名称 |
| --- | --- |
| 6 | `Player` |
| 7 | `Slime` |
| 8 | `CarriedSlime` |
| 9 | `Ground` |
| 10 | `Platform` |
| 11 | `MovingPlatform` |
| 12 | `Hazard` |
| 13 | `Enemy` |
| 14 | `Pickup` |
| 15 | `TriggerZone` |
| 16 | `Gate` |

> 内置的 `Water(4)` 不使用：水与火统一用 `Hazard` 层 + `Hazard.cs` 的 `hazardType` 区分。

### 2.2 Tags（逐个 Add）

```
Player  Slime  Enemy  MovingPlatform  Coin  SlimeOrb
Hazard  Goal  Checkpoint  PressurePlate  Gate
```

---

## 3. Physics 2D 碰撞矩阵

Project Settings → Physics 2D → **Layer Collision Matrix**。

### 3.1 先全部取消

把 6–16 号用户层之间**两两全部取消勾选**（内置层保持默认不勾）。

### 3.2 再勾回下面这些（● 必勾，△ 视关卡需要）

| 层 A | 层 B | |
| --- | --- | --- |
| Player | Ground / MovingPlatform / Platform(△) / Hazard / Enemy / TriggerZone / Gate | ● |
| Slime | Ground / MovingPlatform / Platform(△) / Hazard / Enemy / Pickup / TriggerZone / Gate | ● |
| CarriedSlime | TriggerZone | ● |
| Ground | Enemy | ● |
| Platform | Enemy(△) | △ |
| MovingPlatform | Enemy | ● |
| MovingPlatform | TriggerZone | △（平台压压力板时才勾） |
| Enemy | Gate / TriggerZone | ● |
| Gate | Player / Slime / Enemy | ● |

△ 单向平台：`Player / Slime / Enemy × Platform` 勾上后，平台对象必须加 **PlatformEffector2D**（勾 `Use One Way`），并且平台与角色的 Collider2D 都勾 `Used By Effector`。

### 3.3 必须保持「取消」的关键项（玩法靠它成立）

| 取消项 | 作用 |
| --- | --- |
| `Player × Pickup` | 金币 / 史莱姆球**物理上**就不可能被小孩吃到 |
| `CarriedSlime × 除 TriggerZone 以外全部` | 抱着史莱姆穿尖刺、穿怪物不受伤，也不推挤玩家 |
| `Enemy × Hazard`、`Enemy × Pickup` | 怪物不怕危险区、不吃金币 |
| `Slime × Slime`、`Enemy × Enemy`、`Ground × Ground`、`MovingPlatform × MovingPlatform` | 避免自碰撞抖动 |
| `Hazard / Pickup × Ground / Platform / MovingPlatform` | 触发物不与地形交互 |

### 3.4 其它 Physics 2D 设置

| 项 | 值 |
| --- | --- |
| Gravity | `(0, -9.81)` |
| Velocity Iterations | `8` |
| **Queries Start In Colliders** | **关闭**（否则地面圈检测会命中自己） |
| Queries Hit Triggers | 开启 |
| Default Contact Offset | `0.01` |
| Simulation Mode | Fixed Update |

### 3.5 Sorting Layers（Project Settings → Tags and Layers → Sorting Layers，按顺序加）

```
Background → FarGround → Ground → Platform → Prop → Enemy → Player → Slime → CarriedSlime → Foreground → VFX
```

---

## 4. 输入（旧版 Input Manager）

用到的是 **默认就存在** 的轴与按键，不需要改 Project Settings → Input Manager：

| 用途 | 代码 | 默认配置 |
| --- | --- | --- |
| 左右移动 | `Input.GetAxisRaw("Horizontal")` | 已存在（A/D、←→） |
| 跳跃 | `Input.GetButtonDown("Jump")` | 已存在（Space） |
| 抓取 / 交互（E） | `Input.GetKeyDown(KeyCode.E)` | 无需配置 |
| 投掷 / 次要（Q） | `Input.GetKeyDown(KeyCode.Q)` | 无需配置 |
| 切换物品 | `KeyCode.Alpha1~4` | 无需配置 |
| 暂停 | `KeyCode.Escape` | 无需配置 |

> `PlayerGrab` 的 E / Q 是 **public KeyCode 字段**，想换成手柄键直接在 Inspector 改。

---

## 5. Build Settings 场景顺序

| Index | 场景名 | 说明 |
| --- | --- | --- |
| 0 | `MainMenu` | 主菜单 |
| 1 | `LevelSelect` | 关卡选择 |
| 2 | `Level1` | 教学关 |
| 3 | `Level2` | 携带 / 投掷 / 机关 |
| 4 | `Level3` | 综合关 |

场景名必须与脚本里的 `levelSelectSceneName` / `levelSceneNames[]` / `nextSceneName` 完全一致（大小写敏感）。

---

## 6. 每个关卡场景的固定结构

```
Main Camera                 CinemachineBrain
GameManager                 GameManager 组件
LevelManager                LevelManager 组件（拖好 slime / player / startPoint）
Grid
 ├─ Tilemap_Ground          Tilemap + TilemapRenderer + TilemapCollider2D + CompositeCollider2D + Rigidbody2D(Static)   Layer=Ground
 ├─ Tilemap_Platform        Tilemap + TilemapRenderer + TilemapCollider2D + PlatformEffector2D                        Layer=Platform
 └─ Tilemap_Decoration      Tilemap + TilemapRenderer（无碰撞）
StartPoint                  空物体，放在出生位置
Player                      PlayerController + PlayerInventory + PlayerGrab + Rigidbody2D + CapsuleCollider2D + AudioSource + Animator
 ├─ GroundCheck             空物体，放在脚底
 ├─ CarryPoint              空物体，放在身前上方（例：0.35, 1.0）
 └─ Sprite                  SpriteRenderer
Slime                       SlimeController + SlimePathFollow + Rigidbody2D + CircleCollider2D + AudioSource + Animator
 ├─ GroundCheck             空物体，放在脚底
 └─ Sprite                  SpriteRenderer（SlimeController.spriteRoot 指向它）
WaypointContainer           空物体（路径点实例的父级）
CinemachineVirtualCamera    Follow=Player，Orthographic，Lens Size=8，加 CinemachineConfiner2D（绑定关卡边界的 PolygonCollider2D）
LevelContent                空物体，收纳下面这些
 ├─ Goal / Hazard×n / Coin×n / SlimeOrb×n / Enemy×n
 ├─ MovingPlatform×n / PressurePlate×n / Gate×n / Checkpoint×n
UICanvas                    Canvas(Screen Space - Overlay) + CanvasScaler(1920×1080) + GraphicRaycaster
 ├─ HUD                     UIManager
 ├─ InventoryBar            InventoryUI
 ├─ PausePanel              PauseMenu
 ├─ ResultPanel             LevelResult
 └─ EventSystem             EventSystem + StandaloneInputModule
```

---

## 7. 引用连线表（漏一个就会出现"没反应"）

### Player 相关

| 组件 | 字段 | 拖什么 |
| --- | --- | --- |
| PlayerController | body / groundCheck / spriteRoot / animator / audioSource | 自身 Rigidbody2D / GroundCheck / Sprite / Animator / AudioSource |
| PlayerController | groundLayer | `Ground + Platform + MovingPlatform` |
| PlayerInventory | player | PlayerController |
| PlayerGrab | player / inventory / slime / pathFollow | 同级三个组件 + Slime 上的 SlimePathFollow |
| PlayerGrab | carryPoint | CarryPoint 空物体 |
| PlayerGrab | slimeLayerMask | `Slime + CarriedSlime` |

### Slime 相关

| 组件 | 字段 | 拖什么 |
| --- | --- | --- |
| SlimeController | body / bodyCollider / player / pathFollow / levelManager / groundCheck / spriteRoot / spriteRenderer / animator / audioSource | 对应组件 |
| SlimeController | groundLayer | `Ground + Platform + MovingPlatform` |
| SlimeController | slimeLayer / carriedLayer | `7` / `8` |
| SlimePathFollow | slime / player / waypointPrefab / waypointContainer / audioSource | SlimeController / PlayerController / WaypointMarker 预制体 / WaypointContainer |

### 关卡件

| 组件 | 字段 | 拖什么 |
| --- | --- | --- |
| LevelManager | slime / player / startPoint | Slime / Player / StartPoint |
| Goal | levelManager | LevelManager |
| Hazard | levelManager | LevelManager |
| Enemy | levelManager / levelManager 不需要 slime | LevelManager（史莱姆通过碰撞体自动找到） |
| Checkpoint | levelManager / respawnPoint | LevelManager / 复活点空物体 |
| PressurePlate | platforms / toggleObjects / triggerMask | MovingPlatform 数组 / Gate 物体数组 / `Player+Slime+CarriedSlime+Enemy+MovingPlatform` |
| MovingPlatform | body / points | 自身 Rigidbody2D(Kinematic) / 路点空物体数组 |
| Coin / SlimeOrb | levelManager / slime | LevelManager / SlimeController |

### UI

| 组件 | 字段 | 拖什么 |
| --- | --- | --- |
| UIManager | levelManager / slimeController / playerInventory / playerController / pathFollow | 对应组件 |
| InventoryUI | playerInventory | PlayerInventory |
| LevelResult | levelManager / gameManager | 对应组件；`nextSceneName` 填下一关场景名（末关留空） |
| PauseMenu | gameManager | GameManager |
| MainMenu | — | 只需填场景名 |
| LevelSelectUI | levelButtons / levelSceneNames / backButton | 3 个按钮 / `Level1,Level2,Level3` / 返回按钮 |

---

## 8. 关键参数表（全部是 public 字段，Inspector 可调）

### PlayerController

| 字段 | 值 | 说明 |
| --- | --- | --- |
| moveSpeed | 6.5 | 空手移速 |
| carryMoveSpeed | 3.5 | 携带史莱姆时的移速（绝对值） |
| jumpHeight | 3 | 空手跳 3 格 |
| carryJumpHeight | 1.8 | 携带跳 1.5~2 格 |
| gravityScale | 3 | 决定手感；跳跃初速由它反算 |
| coyoteTime / jumpBufferTime | 0.1 / 0.12 | 跳跃宽容 |
| groundLayer | Ground+Platform+MovingPlatform | 地面判定 |

### SlimeController

| 字段 | 值 | 说明 |
| --- | --- | --- |
| maxHealth / currentHealth | 100 / 100 | 血量 |
| followSpeed | 6 | 接近玩家移速 |
| followDistance | 1.4 | 跟随距离 |
| followDelay | 0.15 | **0.1–0.2s** 移动延迟 |
| jumpFollowDelay | 0.2 | **0.15–0.25s** 跟跳延迟 |
| jumpHeightRatio | 1.0 | 跟跳高度 = 玩家跳高 × 该值 |
| maxStepHeight | 0.35 | 高过它就卡住等玩家 |
| stopAtLedge | true | 深坑前停在边缘 |
| wallCheckDistance / ledgeCheckForward / ledgeCheckDistance | 0.45 / 0.45 / 0.9 | 障碍与悬崖检测 |
| invincibleTime / scaredDuration | 1.0 / 0.8 | 无敌帧 / 惊吓时长 |
| recallSpeedMultiplier | 1.4 | 哨子召回倍速 |
| groundLayer | Ground+Platform+MovingPlatform | 地面判定 |

### LevelManager

| 字段 | 值 | 说明 |
| --- | --- | --- |
| basePrice | 100 | 基础价 |
| parTime | 60 | 标准通关时间 |
| timeBonusMax / timeBonusMin | 1.5 / 0.5 | 时间奖励上下限 |
| starThresholds | 60 / 120 / 180 | 一 / 二 / 三星金币门槛 |
| failRestartDelay | 1.5 | 史莱姆死亡后重开延迟 |
| playerRespawnDelay | 1.0 | 玩家从检查点复活延迟 |
| levelIndex | 1/2/3 | 决定解锁下一关 |

**结算公式**：`最终金币 = basePrice × (当前血量 / 最大血量) × 时间奖励`
时间奖励：`t ≤ parTime` 时从 `timeBonusMax` 线性降到 `1.0`；`t > parTime` 时从 `1.0` 继续降到 `timeBonusMin`。

---

## 9. Animator 参数名（建好即可，缺参数只会打警告）

| Animator | 参数 | 类型 |
| --- | --- | --- |
| Kid.controller | `Speed`(float)、`IsGrounded`(bool)、`IsCarrying`(bool)、`IsDead`(bool) | |
| Slime.controller | `Speed`(float)、`IsGrounded`(bool)、`IsCarried`(bool)、`IsScared`(bool)、`IsDead`(bool) | |

没有美术资源时可以先不建 Animator，把 `animator` 字段留空即可。

---

## 10. 冒烟测试（建议按顺序做，每步都能独立验证）

**测试场景 T1（只测玩家）**
1. 空场景：Grid + Tilemap_Ground（Layer=Ground）+ Player + GameManager + LevelManager + Cinemachine。
2. Play：能跑、能跳，跳跃最高点约 3 格；从 3 格落差跳下能落地。

**测试 T2（抓取 + 跟随）**
3. 放一个 Slime（Layer=Slime）+ WaypointContainer，把 Slime 的 groundLayer 设为 Ground。
4. Play：史莱姆贴地跟在身后约 1.4 格，明显慢半拍；玩家跳起来后约 0.2s 史莱姆跟跳。
5. 走近史莱姆按 E：被抓起来挂在 CarryPoint 上，移速变慢、跳变低；再按 E 放下。

**测试 T3（障碍与路径点）**
6. 用 Tilemap 画一个 2 格高的台子：史莱姆走到墙边停住不动（卡住 = 正确表现）。
7. 玩家跳上台子：史莱姆延迟跟跳失败 → 停在台下（正确，需要玩家处理）。
8. 物品栏切到哨子（按 2）→ 按 E 切成 Stay，史莱姆原地不动；再按 E 切回 Follow，史莱姆走过来。
9. 物品栏切到引导石（按 3）→ 在几处按 E 放 3 个路径点 → 按 E 变 Stay → 再按 E 变 Follow：史莱姆按路径点顺序依次走完，最后走到玩家身边。

**测试 T4（伤害与结算）**
10. 放一个 Hazard（Layer=Hazard，isTrigger，damage=15）：史莱姆碰到掉血 + 闪红 + 停一下；玩家碰到按 `killPlayer` 死亡。
11. 放一个 Goal（Layer=TriggerZone，isTrigger）：史莱姆走进去 → 控制台/Inspector 能看到 `LevelFinished=true`，`FinalCoins` 有值。
12. 把 Hazard 的 damage 改成 100：史莱姆碰到 → 血量归零 → 1.5s 后自动重开关卡。

**测试 T5（完整流程）**
13. 建 5 个场景并加入 Build Settings，接上 UI 面板，跑通：主菜单 → 选关 → 关卡 → 结算 → 下一关。

---

## 11. 常见问题排查

| 现象 | 原因 |
| --- | --- |
| 金币小孩走过去也能吃 | `Pickup × Player` 矩阵没取消 |
| 抱着史莱姆还会被尖刺扣血 | `CarriedSlime × Hazard` 没取消，或 `SlimeController.carriedLayer` 没填 8 |
| 史莱姆不跟跳 | `player.LastJumpTime` 需要对上：确认 SlimeController.player 已拖；`jumpFollowDelay` 过大也会错过 |
| 史莱姆原地抖动 | 地面检测圈太大导致反复离地：调小 `groundCheckRadius`，或确认 `Queries Start In Colliders` 已关闭 |
| 史莱姆被小台阶卡住 | 提高 `maxStepHeight`（但不要超过 0.5，否则等于允许攀爬） |
| 史莱姆穿过平台掉下去 | 单向平台没加 PlatformEffector2D，或 Collider 没勾 `Used By Effector` |
| 站在移动平台上会抖 | 关掉 `MovingPlatform.carryRider` 试试，或把乘客改为 parent 到平台 |
| 按 E 抓不到史莱姆 | `grabRadius` 太小或 `slimeLayerMask` 没勾 `Slime`；默认 1.2 已经比较宽容 |
| 结算面板不出现 | `LevelResult.levelManager` 没拖；结算靠 UI 轮询 `LevelFinished`，不拖就没人通知它 |
| 暂停后时间不恢复 | 确认"回到游戏"按钮调用的是 `GameManager.SetPaused(false)` |

---

## 12. Unity 2022.3 LTS 版本适配说明

- **刚体速度统一用 `Rigidbody2D.velocity`**（Unity 2022.3 的唯一写法，全工程 31 处）。
  若将来升级到 Unity 6，需要把 `velocity` 全局改成 `linearVelocity`（分布在 PlayerController / SlimeController / Enemy）。
- **Cinemachine**（2022.3 用 2.x 版本）：Package Manager → Unity Registry → 安装 **Cinemachine**（2.9.x / 2.10.x）。
  主相机挂 **CinemachineBrain**；跟随相机用 **CinemachineVirtualCamera**（`CinemachineCamera` 是 Unity 6 的新名字）；
  限制相机不出关卡边界用 **CinemachineConfiner2D**（Cinemachine 2.6 及更早版本里叫 `CinemachineConfiner`）。
- **Active Input Handling 必须选 `Input Manager (Old)` 或 `Both`**（Project Settings → Player → Other Settings）。
  如果装了新 Input System 包并选成 `Input System Package (New)`，`Input.GetAxisRaw` / `Input.GetButtonDown` 会直接抛异常，
  本 Demo 的旧版输入写法就无法工作。
- **TextMeshPro**：首次使用会提示导入，点 **Import TMP Essentials**。
- **RuleTile**（可选，用于地形自动拼接）：Package Manager 安装 **2D Tilemap Extras**。
- 本 Demo 不依赖任何 URP 专有特性，Built-in 渲染管线的 2D 工程同样可以直接跑。

---

## 13. 验收对照（对应玩法规则）

| 规则 | 落地位置 |
| --- | --- |
| 史莱姆贴地蠕动、不自主跳跃、只跟跳 | `SlimeController.MoveTowards` / `CheckJumpFollow` |
| 跟随有 0.1–0.2s 延迟 | `followDelay` + 位置历史缓冲 |
| 跟跳延迟 0.15–0.25s | `jumpFollowDelay` + `PlayerController.LastJumpTime` |
| Follow / Stay 由玩家主动切换，放下不自动改模式 | `SlimeController.SetMode` / `PlayerGrab` 的放置分支 |
| Stay→Follow 按放置顺序走完并最后走到人身边 | `SlimePathFollow.BeginFollowWithPlayerAsLast` |
| 路径点可绑在运动物体上 | `WaypointMarker.followTarget` 或直接做子物体 |
| 不能攀爬 / 寻路 / 绕路 / 传送 | 全工程无 A*、NavMesh、无传送调用；`maxStepHeight` 限制迈步高度 |
| 抓取半径 1.2 且宽容 | `PlayerGrab.grabRadius` + 层判定 + 距离兜底 |
| 危险区掉血 + 无敌帧 | `Hazard.damage` + `SlimeController.invincibleTime` |
| 血量越低钱越少 | `LevelManager.FinalCoins` |
| 血量归零关卡失败自动重开 | `SlimeController.Die` → `LevelManager.OnSlimeDied` |
| 收集物只对史莱姆生效 | 碰撞矩阵 + `Coin` / `SlimeOrb` 的组件判定 |
| 携带时移速减慢、跳跃变低、不能切物品 | `PlayerController.carryMoveSpeed/carryJumpHeight` + `PlayerInventory.IsSwitchLocked` |
| 玩家碰怪死亡从检查点复活并保留金币 | `Enemy.HandleContact` → `LevelManager.OnPlayerDied` → `PlayerController.Respawn` |
| 0–1.5 / 1.5–3 / 3+ 格的分层设计 | `jumpHeight=3`、`carryJumpHeight=1.8`、投掷与机关 |

