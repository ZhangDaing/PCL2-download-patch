# PCL2 MC Download Restore Patch

修复 Plain Craft Launcher 2 **2.99.0.0** 及其后续版本中「下载原版 Minecraft」功能被移除的问题。

> **本项目的绝大部分代码、文档与测试由 AI（Claude / DeepSeek 驱动的编码代理）生成。**
> 详见下方 [AI 生成内容声明](#ai-生成内容声明)。

---

## 这是什么

PCL 2.99.0.0 在 `ModSecret.vb` 里加入了：

```vb
Public Const RestrictMcDownload As Boolean = True
```

由于 `Const` 是**编译期内联**的，VB 编译器把 `If True Then ... Else ...` 的 `Else`
分支当作死代码**整段删除**。结果是在 2.99 的 IL 里，真正加载原版版本列表的那行
`DlClientListLoader` 绑定**已不复存在**。

本项目提供一个 IL 层补丁器，把这一处引用重建回来：

| | `PageDownloadInstall.LoaderInit` |
|---|---|
| 2.14.0.0 官方 | 21 条指令，直接 `ldsfld DlClientListLoader` |
| 2.99.0.0 官方 | 30 条指令，构造一个直接 `throw` 的假 loader |
| **打补丁后** | 18 条指令，`ldsfld DlClientListLoader` |

关键点：`ModDownload.DlClientListLoader` 字段本身在 2.99 里**依然完整存在**，
被删掉的只是 `PageDownloadInstall` 对它的引用。因此补丁只需重建引用，无需注入新代码。

---

## 用法

1. **完全关闭 PCL**
2. 下载并解压本仓库
3. 双击 `apply.bat`
4. 若自动查找失败：
   ```
   apply.bat "D:\minecraft\Plain Craft Launcher 2.exe"
   ```
5. 启动 PCL → 下载页 → 应能看到原版版本列表

**还原**：双击 `restore.bat`，或手动把 `Plain Craft Launcher 2.exe.orig` 改回原名。

**查看状态**：
```
bin\Verify.exe "D:\minecraft\Plain Craft Launcher 2.exe"
```
输出 `PASS` 表示补丁生效，`FAIL` 表示未打补丁。

---

## 安全机制

补丁器并非盲目改字节：

1. **形态识别**：必须同时满足「存在限制文案特征」且「不存在真实加载器引用」才动手
2. **已是补丁版则直接跳过**，不会二次修改
3. **形态不识别则报错退出**，目标文件一个字节都不动
4. **外部跳转检测**：若待删除 IL 区间被其他指令跳转引用，则中止
5. **写临时文件 → 回读校验 → 才替换**，全程不覆盖原文件直到成功
6. **自动备份**为 `.orig`

所有失败路径都保证**原文件不变**。

---

## 已知缺陷

### 1. PCL 更新后补丁失效（无解）
PCL 更新是整包替换 exe，任何字节级修改都会丢失。**更新后需重新运行 `apply.bat`。**

补丁器用特征匹配而非硬编码偏移，通常可跨小版本使用；若 PCL 重构了
`LoaderInit`，补丁器会**安全拒绝**（不会写坏文件），届时需更新本工具。

### 2. 只修复「原版版本列表加载」这一处
本补丁只改 `LoaderInit`。以下位置**未改动**：

- 整合包安装（`ModModpack`）
- 版本设置里的「修改 MC 版本」卡片（`PageDownloadModify`）
- 资源详情页整合包「安装」按钮（`PageDownloadCompDetail`）

### 3. exe 哈希改变
补丁后文件哈希与官方发布版不同。PCL 2.99 源码中未见完整性自校验，
但不排除未来版本加入。

### 4. 杀毒软件可能误报
补丁器会修改 PE 文件，部分杀软可能对产物或工具本身报警。
源码在 `src/` 下，可自行审阅与编译。

### 5. 仅支持与 PCL 相同的环境
Windows + .NET Framework 4.8。

---

## AI 生成内容声明

**本项目是 AI 辅助生成的产物。**

- 补丁器（`src/Patcher.cs`）、校验器（`src/Verify.cs`）、批处理脚本、
  本文档以及全部验证过程，均由**人工智能编码代理**在人类用户的
  需求指导下生成
- 生成过程包含：源码比对、IL 反汇编分析、补丁实现、红/绿测试、端到端验证
- 人类用户负责：提出需求、确认技术路线、验收结果

**这意味着：**

- 代码可能包含错误、遗漏或非最优实现，**请在使用前自行审阅**
- 本项目不提供任何形式的担保（见 [LICENSE](LICENSE)）
- AI 生成的内容可能存在与现有项目雷同之处，如涉及版权问题请提出 Issue

---

## 免责声明

> **请务必阅读**

1. **本项目与 PCL 官方（龙腾猫跃 / Meloong）无任何关联。**
   本项目不是 PCL 的官方项目、分支或后续版本，未获得官方认可或授权。

2. **本项目不包含、也不分发 PCL 的任何二进制文件。**
   补丁器在运行时从用户**本机已安装**的 PCL exe 中提取所需的依赖程序集，
   仅在本机临时目录使用。仓库中唯一附带的二进制是 `Mono.Cecil.dll`
   （MIT 许可证，第三方开源库）。

3. **使用风险由使用者自行承担。**
   修改可执行文件属于非标准操作，可能导致程序损坏、无法启动或数据丢失。
   作者不对任何直接或间接损失负责。**请务必保留备份。**

4. **本项目不鼓励、不背书任何违反服务条款或法律法规的行为。**
   PCL 移除该功能是开发者的主动决定（据其发布说明，出于相关法律法规考虑）。
   本项目仅针对用户**本机已合法持有**的软件提供技术层面的修改方案，
   用于**个人研究学习**。
   **请勿分发修改后的 PCL 可执行文件。**

5. **本项目按「现状」提供，不提供任何明示或暗示的担保。**

---

## 开源许可

本项目代码以 [MIT License](LICENSE) 发布（不含 `bin/Mono.Cecil.dll`，
该文件遵循其自身的 MIT 许可证）。

第三方组件：

| 组件 | 许可证 | 用途 |
|---|---|---|
| [Mono.Cecil](https://github.com/jbevain/cecil) | MIT | IL 读写 |
| PCL / Plain Craft Launcher | 见其 LICENCE | **本项目不分发，仅在用户本机操作** |

---

## 技术细节

<details>
<summary>点击展开：为什么不能直接改常量</summary>

`RestrictMcDownload` 是 `Const`（VB 的编译期常量）。编译器会把它的值
**内联**到每一个使用点，并据此做死代码消除。因此：

- 在 exe 的元数据里**找不到**这个字段（它只作为字面量存在）
- 不存在「把 True 改成 False」这种改法
- 只能逐个修改使用点的 IL

本补丁只针对唯一影响版本列表加载的那一处。
</details>

<details>
<summary>点击展开：补丁做了什么</summary>

在 `PageDownloadInstall.LoaderInit` 中：

```
删除：ldstr "关闭下载"
      ldsfld  $IR3-3 / brfalse / ldsfld / ldftn / newobj Action`1  (缓存回调)
      ldnull / ldc.i4.2 / newobj LoaderTask`2::.ctor

插入：ldsfld ModDownload::DlClientListLoader
```

该区间的计算栈净变化为 0（原压入 1 个 `LoaderTask`，新压入 1 个 `LoaderTask`），
后续 `PageLoaderInit` 调用的泛型实参完全不受影响。
</details>
