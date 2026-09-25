# 第三方组件与许可（Third-Party Notices）

> 这份文件回答一个问题：**仓库里哪些东西不是我自己写的？各自是什么许可？**
> 结论先说：**随本仓库分发的第三方资源只有中文字体（思源黑体）**；Unity / URP / TextMesh Pro **不随仓库分发**；
> 其余美术与音效都由本项目的生成器脚本产出（见第 3 节）。

## 0. 一览

| 组件 | 随仓库分发？ | 许可 | 位置 |
|---|---|---|---|
| 中文字体 **Source Han Sans SC（思源黑体）** | ✅ 是 | **SIL OFL 1.1**（见 §1） | `Assets/_Project/Art/UI/` |
| Unity / URP / TextMesh Pro | ❌ 否（使用者自行安装） | Unity 自己的许可条款 | `Packages/manifest.json` 里只声明依赖 |
| 本项目美术（像素素材 / UI 图标 / 象形图路牌） | ✅ 是（自产） | 与代码同许可（MIT） | `Editor/PixelArtGenerator.cs`、`Editor/MuralGenerator.cs` |
| 本项目音效（24 个 WAV） | ✅ 是（自产） | 与代码同许可（MIT） | `Editor/AudioGenerator.cs` |

---

## 1. 中文字体：Source Han Sans SC（思源黑体）

- **随仓库分发的文件**：`Assets/_Project/Art/UI/` 下的字体文件（`.ttf` / `.otf`）与其 TMP 字体资产。
- **版权行（Adobe，逐字）**：

      Copyright 2014-2021 Adobe (http://www.adobe.com/), with Reserved Font Name 'Source'.

- **许可**：**SIL Open Font License, Version 1.1**（OFL 1.1）。按 OFL 1.1，可以自由使用、研究、修改、再分发（含商用），
  **但不得单独出售字体本身**，且再分发时必须保留上面的版权行与本许可全文（见下）。
- ⚠️ **`Reserved Font Name` 条款**：修改这个字体之后，**不能再继续使用 "Source" 这个名字**。
- 说明：**本仓库不再包含 `SimHei`（中易黑体）** —— 它随 Windows 分发、不允许再分发，已移除。

### SIL OFL 1.1 全文（逐字，未改写）

```-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide development of collaborative font projects, to support the font creation efforts of academic and linguistic communities, and to provide a free and open framework in which fonts may be shared and improved in partnership with others.

The OFL allows the licensed fonts to be used, studied, modified and redistributed freely as long as they are not sold by themselves. The fonts, including any derivative works, can be bundled, embedded, redistributed and/or sold with any software provided that any reserved names are not used by derivative works. The fonts and derivatives, however, cannot be released under any other type of license. The requirement for fonts to remain under this license does not apply to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright Holder(s) under this license and clearly marked as such. This may include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the copyright statement(s).

"Original Version" refers to the collection of Font Software components as distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting, or substituting -- in part or in whole -- any of the components of the Original Version, by changing formats or by porting the Font Software to a new environment.

"Author" refers to any designer, engineer, programmer, technical writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining a copy of the Font Software, to use, study, copy, merge, embed, modify, redistribute, and sell modified and unmodified copies of the Font Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components, in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled, redistributed and/or sold with any software, provided that each copy contains the above copyright notice and this license. These can be included either as stand-alone text files, human-readable headers or in the appropriate machine-readable metadata fields within text or binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font Name(s) unless explicit written permission is granted by the corresponding Copyright Holder. This restriction only applies to the primary font name as presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font Software shall not be used to promote, endorse or advertise any Modified Version, except to acknowledge the contribution(s) of the Copyright Holder(s) and the Author(s) or with their explicit written permission.

5) The Font Software, modified or unmodified, in part or in whole, must be distributed entirely under this license, and must not be distributed under any other license. The requirement for fonts to remain under this license does not apply to any document created using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM OTHER DEALINGS IN THE FONT SOFTWARE.
```

> 上面这段许可正文与仓库内 `Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt` 里的 SIL OFL 1.1 正文**逐字一致**
> （那份文件是 Unity TextMesh Pro 自带的 Liberation 字体许可，内容就是标准的 OFL 1.1 正文）。

---

## 2. Unity / URP / TextMesh Pro —— **不随本仓库分发**

- 本仓库只有**工程源码与资源**；Unity Editor、URP 包、TextMesh Pro 包都**不在仓库里**（`Library/` 也已被 `.gitignore` 忽略）。
- `Packages/manifest.json` **只声明依赖**（如 `com.unity.render-pipelines.universal`、`com.unity.textmeshpro`），由使用者本地安装；
  **这些软件的使用受 Unity 自己的许可条款约束**，与本仓库的 MIT 无关。
- 仓库里 `Assets/TextMesh Pro/**` 下确有随 TMP 包导入的资源与许可文件
  （`Fonts/LiberationSans - OFL.txt`、`Sprites/EmojiOne Attribution.txt`），它们各自保留了原始许可。

---

## 3. 本项目自产素材（无第三方素材依赖）

| 类别 | 生成器 | 复现菜单 |
|---|---|---|
| 像素素材 / UI 图标 | `Assets/_Project/Editor/PixelArtGenerator.cs` | `Tools/呆呆史莱姆/4 生成素材/像素素材` |
| 象形图路牌（20 张，64×64） | `Assets/_Project/Editor/MuralGenerator.cs` | `Tools/呆呆史莱姆/4 生成素材/象形图路牌` |
| 音效（24 个 WAV） | `Assets/_Project/Editor/AudioGenerator.cs` | `Tools/呆呆史莱姆/4 生成素材/音效素材` |

> 这三条都是**程序化生成**：仓库里没有外部素材包，也就没有素材版权问题。要核对就打开对应的生成器脚本。

---

## 4. 代码许可

本项目代码采用 **MIT**，见 [`LICENSE`](LICENSE)。
