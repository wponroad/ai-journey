# W01 · Day 1　Token 实测：中文 vs 英文

> 日期：2026-11-02（周一）　工具：SharpToken（`cl100k_base`）　样本：同一段 AI 主题文字的中英两版  
> 目的：搞清 token 是什么、中英文各占多少、以及「用 SharpToken 估 DeepSeek 成本」是否靠谱。

## 实验原文（实验样本，勿删）

### 中文（389 字符 = 358 汉字 + 31 标点 → 459 token）

```
人工智能正在深刻改变我们的生活方式。从智能手机中的语音助手，到自动驾驶汽车，再到医疗影像的辅助诊断，人工智能技术已经渗透到社会的各个角落。然而，技术的快速发展也带来了新的挑战，例如数据隐私保护、算法偏见以及就业结构的变化。如何在推动创新的同时建立合理的监管框架，是各国政府和企业共同面对的问题。许多研究者认为，人工智能的未来发展不应仅仅追求性能的提升，还应关注其可解释性、公平性和安全性。教育体系也需要相应调整，培养具备跨学科能力的人才，使人们能够理解并负责任地使用这些工具。与此同时，公众对人工智能的认识仍然有限，科普工作显得尤为重要。只有当技术开发者、政策制定者和普通用户之间形成有效的沟通，人工智能才能真正造福社会。回顾历史，每一次重大的技术革命都伴随着阵痛，但也最终推动了人类文明的进步。面对人工智能，我们既不应盲目乐观，也不应过度恐惧，而应以审慎而开放的态度迎接变化。
```

### 英文（1541 字符 = 204 词 + 27 标点 → 239 token）

```
Artificial intelligence is profoundly changing the way we live. From voice assistants on smartphones to self-driving cars and AI-assisted medical imaging diagnosis, artificial intelligence technology has permeated every corner of society. However, the rapid development of technology also brings new challenges, such as data privacy protection, algorithmic bias, and changes in employment structure. How to establish a reasonable regulatory framework while promoting innovation is a problem that governments and enterprises around the world must face together. Many researchers believe that the future development of artificial intelligence should not merely pursue performance improvements, but should also focus on its interpretability, fairness, and safety. Education systems also need to be adjusted accordingly to cultivate talents with interdisciplinary capabilities, enabling people to understand and use these tools responsibly. At the same time, public understanding of artificial intelligence remains limited, making science popularization particularly important. Only when effective communication is established among technology developers, policymakers, and ordinary users can artificial intelligence truly benefit society. Looking back at history, every major technological revolution has been accompanied by pain, but has ultimately driven the progress of human civilization. Facing artificial intelligence, we should neither be blindly optimistic nor overly fearful, but should embrace change with a prudent and open attitude.
```

## 一、实测数据

| 指标                 | 中文         | 英文        | 比值           |
| ------------------ | ---------- | --------- | ------------ |
| 总字符                | **389**    | **1541**  | 1 : 4.0      |
| **汉字 / 英文词**       | **358 汉字** | **204 词** | 1.75 : 1     |
| 标点                 | 31         | 27        | —            |
| **token 数**        | **459**    | **239**   | **1.92 : 1** |
| **token / 汉字 · 词** | **1.28**   | **1.17**  | 约 1.1 : 1    |
| token / 字符         | 0.85       | 0.155     | 5.5 : 1      |

**同样一段意思，中文的字符数只有英文的四分之一，token 数却是英文的 1.92 倍。**

**但看中间那行：一个汉字约 1.28 token，一个英文词约 1.17 token——单位成本几乎一样。** 中文贵不在这里，贵在**单位数量**：同样一段意思，中文要 358 个单位，英文只要 204 个，多了 75%。

按 token 反推汉字构成（假设汉字只占 1 或 2 个 token）：**约 72% 的汉字是 1 个 token，约 28% 被拆成 2 个。**

## 二、切分结果（节选）

完整输出有 459 / 239 个 token，太长不适合贴全文，下面各留前 40 个作为样本（需要完整数据随时重跑 `TokenLab`）。

### 中文

```
[人][工][?][?]能[正在][?][?][?][?][改][变][我们][的][生][活][方式][。][从][?][?]能[手机][中][的][语][音][?][?]手[ ，][到][自][动]...
```

### 英文

```
[Art][ificial][ intelligence][ is][ profoundly][ changing][ the][ way][ we][ live][.][ From][ voice][ assistants][ on][ smartphones][ to][ self][-driving][ cars][ and][ AI][-ass][isted]...
```

### 三个观察

**1. `[?]` 是半个汉字。** 一个汉字在 UTF-8 里占 3 个字节，tokenizer 按字节切，切在中间就还原不出完整的字——单独 decode 只能得到乱码。中文里大量汉字需要 2 个 token 才拼得出来。

**2. 常见双字词会被合并。** `[正在]`、`[我们]`、`[手机]`、`[数据]`、`[例如]`、`[同时]` 都是一个 token。词表里收了这些高频词，但覆盖不全，所以同样长度的词有的合一、有的拆开。

**3. 英文的空格被吃进 token 尾部。** `[ intelligence]`、`[ is]`、`[ From]` 前面的空格不是独立 token，而是粘在前一个 token 后面。所以单词边界和 token 边界并不对齐。同样是长单词，`[ profoundly]` 一个 token，`[Art][ificial]` 两个——取决于词表里有没有整词条目。

## 三、为什么中文这么费 token

因为 `cl100k_base` 是 **OpenAI 给 GPT-3.5/GPT-4 用的词表**，`cl100k` 意思是「词表 10 万条」。它是**按英文构造的**，中文覆盖不全——你那 358 个汉字里约三成在词表里没有条目，只能退回字节级切分，拆成 2 个 token 才拼回一个字。

但这不是主因。主因是**单位数量的差异**：同样的信息量，中文写成 358 个字，英文写成 204 个词。中文一个字大致对应一个词素，英文一个词往往含多个词素——所以中文的「单位」更碎，单位数就更多。

同样一个道理反过来看：国产模型（DeepSeek、Qwen、GLM）训练时中文语料占比高，词表里中文高频词更丰富，效率反而比英文词表处理中文好得多。

**结论：token 数不由文字决定，由 tokenizer 决定。换模型 = 换度量衡。**

## 四、换成钱

中文 1000 字 ≈ 1180 token（cl100k 口径）。按 DeepSeek 空闲时段输入价 1 元 / 百万 token：

> **一页 1000 字的中文文档 ≈ 0.12 分钱**（约 1 厘）

一千页一块二。这个数量级要记住，以后估成本直接套。

## 五、坑：用 SharpToken 估 DeepSeek，会高估近一倍

|                           | 中文 token / 字符 |
| ------------------------- | ------------- |
| SharpToken（cl100k_base）实测 | **1.18**      |
| DeepSeek 官方文档口径           | **0.60**      |
| 偏差                        | **约 1.97 倍**  |

DeepSeek 用的是自研 BBPE（词表从 V2 的 10 万扩到 V3/R1 的 **129,280**），**不在 tiktoken 体系里**，也没有可用的 `encodingName`。

**所以周五的成本计算器必须这样设计：**

```
预估（发送前）→ 按 0.6 token/中文一字粗算，标注「估算值 ±30%」
实算（发送后）→ 读 response.usage 的 prompt_tokens / completion_tokens
```

**切勿把估算值当账单值写进报告**——误差接近一倍且无人能察觉。

> 官方文档：<https://api-docs.deepseek.com/zh-cn/quick_start/token_usage>  
> （页面上还提供 `deepseek_tokenizer.zip` 可离线精确计算，但是 Python 脚本；.NET 侧 `Microsoft.ML.Tokenizers` 没有通用的 HuggingFace `tokenizer.json` 加载器）

## 六、今天的三条结论

1. **一个汉字 ≈ 1.28 token，一个英文词 ≈ 1.17 token**（cl100k 口径）——单位成本几乎一样
2. **同样意思，中文 token 是英文的约 1.9 倍**。原因不是「中文贵」，是**中文需要的单位更多**（358 字 vs 204 词），加上约三成汉字被拆成 2 个 token
3. **SharpToken 用 OpenAI 口径，估 DeepSeek 会偏高近一倍**（1.18 vs 官方 0.60），必须实测或读 `usage`

---

## 附：一次「AI 给出的数字不可信」的现场记录

我让 DeepSeek 在线「切分」同一段文本，它输出了一张逐句 token 估算表和一段结论。**但那些数字是它估的，不是算的**：

| 项目 | DeepSeek 在线估算 | 我的 SharpToken 实测（cl100k） | DeepSeek API usage（待补） |
|---|---|---|---|
| 中文 token | 约 340–370 | **459** | 待实测 |
| 英文 token | 约 240–270 | **239** | 待实测 |
| 中文 / 英文倍数 | 「约 1.3–1.5 倍」 | **1.92 倍** | 待实测 |
| 中文字符数 | 「约 300 字」 | **389**（358 汉字 + 31 标点） | 同左 |

> **三列是三种口径，不能混用：**
> - 第二列：模型凭印象估的，**不可信**
> - 第三列：SharpToken 用 OpenAI 词表算的，**能看切分规律，但估 DeepSeek 成本会偏高**
> - 第四列：DeepSeek 服务端真实计费口径，**唯一能拿去算钱的数**。等下次真实调用 API 时，把 `response.usage.prompt_tokens` 打出来填这里

它自己在末尾也承认：「如果告诉我具体分词器，我可以给你一段 Python 代码，在你本地得到**精确**的逐句 token 数」——**说明它知道自己是估的，但前面那张表格和结论写得像结论。**

这正好是 W0 第一题里说的**静默失败**：格式规范、表格齐整、语气笃定，数字是错的。

**记住这个教训：凡是要精确数的东西，不要问模型，要跑代码。**
