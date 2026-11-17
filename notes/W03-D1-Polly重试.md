# W03-D1　Polly 指数退避重试

> 2026-11-16　目标：让 LLM 调用能扛住 429 / 500 / 超时，而不是一遇故障就崩
> 一句话结论：**重试不是"再试一次"，是一套有代价的工程决策——什么值得重试、等多久、等几次、谁承担代价。**

## 一、今天的分界线

前三周解决的是一件事：**让模型输出可控**。

| 周 | 解决的问题 | 失败时的表现 |
|---|---|---|
| W1 | 能不能调用 | 调用失败 → 抛异常，程序崩 |
| W2 | 输出结构对不对 | 解析失败 → 重试 prompt / 加兜底 |
| **W3** | **服务不响应怎么办** | **网络层故障 → 整条链路一起完蛋** |

前面的代码都有一个隐含假设：**服务端会好好回答**。

今天开始处理另一个问题：**它会不好好回答**。而这件事你控制不了，只能提前准备。

## 二、Polly 的两代 API —— 官网看不懂的真正原因

Polly 在 2024 年发布 v8，**整套 API 重写了**，但旧文档没删，两套并存且没有明显提示。

| | v7（旧） | v8（新） |
|---|---|---|
| 核心概念 | `Policy`（策略对象） | `ResiliencePipeline`（弹性管道） |
| 组合方式 | `Policy.Wrap(...)` | `builder.AddRetry().AddTimeout()` 链式 |
| 典型写法 | `Policy.Handle<X>().WaitAndRetryAsync(3, ...)` | `new ResiliencePipelineBuilder().AddRetry(...).Build()` |
| 包名 | `Polly` | **`Polly.Core`** |

**我第一版给的示例就是 v7 的语法**（`Policy.Handle<>().WaitAndRetryAsync`）。如果装的是 `Polly.Core`，这段代码编译不过，而报错信息看不出原因——它只会说找不到方法。

**包名的坑**：`Polly` 含 v8 + v7 兼容层，`Polly.Core` 是纯 v8。新项目用后者。

## 三、Polly 只做一件事：把一段委托包起来

它不认识 HTTP，更不认识 LLM。它唯一认的是：

```csharp
await pipeline.ExecuteAsync(async ct => { /* 可能失败的代码 */ }, ct);
```

真正的配置只有三个旋钮：

| 要回答的问题 | 字段 | 注意 |
|---|---|---|
| 什么算失败 | `ShouldHandle` | 比另外两个都重要 |
| 最多试几次 | `MaxRetryAttempts = 3` | **总共执行 4 次**（1 原始 + 3 重试） |
| 每次等多久 | `Delay` + `BackoffType` + `UseJitter` | 见第六节 |

`MaxRetryAttempts` 这个差一错误非常普遍。写"重试 3 次"时，实际请求量是 4 倍。

## 四、什么值得重试 —— 这一项比参数重要

重试只对**瞬时故障**有意义：

| 故障 | 该重试吗 | 原因 |
|---|---|---|
| 429 限流 | ✅ | 等一会儿就好 |
| 500 / 502 / 503 / 504 | ✅ | 服务端临时挂了 |
| 超时 | ⚠️ | 可能好，但代价大（第十节） |
| **400 参数错** | ❌ | 重试一百次还是 400 |
| **401 key 无效** | ❌ | 同上 |

所以 `ShouldHandle` 不能写成"任何 `HttpRequestException` 都重试"，要按状态码筛：

```csharp
ShouldHandle = new PredicateBuilder()
    .Handle<HttpRequestException>(ex => ex.StatusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout)
    .Handle<TaskCanceledException>(),
```

**这条和 W1 的 `max_tokens` 静默失效是同一类教训**：参数错了，重试再多次也没用，只是多花几笔钱。

## 五、为什么是"指数"

固定间隔：1s、1s、1s。问题是 **429 的含义就是"你太快了"**，1 秒后重试还是太快。

指数退避公式：

```
第 n 次重试前等待 = Delay × 2^(n-1)

Delay = 1s  ⇒  1s → 2s → 4s → 8s → 16s ...
```

**为什么翻倍**：你不知道对方什么时候恢复。翻倍是用最少的总等待覆盖任意长度故障的策略——持续 5 秒的故障第 3 次赶上，持续 1 分钟的故障第 6 次也赶上。

**代价是失控**：第 10 次重试要等 8 分半。所以必须配 `MaxDelay` 封顶。

## 六、抖动：实测结果和我预想的不一样（今天最有价值的一条）

### 实测数据

`failTimes: 2`，前两次 429：

```
[flaky] 第 1 次调用 → 抛 429
[retry 1] 等 1.4s
[flaky] 第 2 次调用 → 抛 429
[retry 2] 等 0.8s          ← 比上一次还短！
[flaky] 第 3 次调用 → 放行
```

**等待时间不是递增的：1.4s → 0.8s。**

### 原因：Polly 的 `UseJitter` 不是"指数加随机"，是"取代指数"

我以为的：`1s × (1 + 随机)` ≈ 1.0~1.3s，递增但带偏移。

实际用的算法叫 **decorrelated jitter**（去相关抖动）：

```
第 n 次等待 = random( Delay , 上次等待 × 3 )，再用 MaxDelay 封顶

第 1 次：random(1s, 3s)   → 1.4s
第 2 次：random(1s, 4.2s) → 0.8s    ← 合法
```

下界永远是 `Delay`，上界是"上次等待的 3 倍"。**单次可增可减，长期趋势增长。**

这是 AWS 2015 年那篇 *Exponential Backoff And Jitter* 里的推荐算法，Polly 直接采用了。它比纯指数好，因为它把"上次等了多久"也纳入随机，客户端之间分布更散。

### 三种抖动的区别

| 方式 | 序列 | 特点 |
|---|---|---|
| 无抖动 | 1, 2, 4, 8 | 严格递增，但所有客户端同步 → 重试风暴 |
| Full jitter | random(0, 2ⁿ) | 平均等待砍半，但可能等太久 |
| **Decorrelated（Polly 用的）** | random(base, prev×3) | 单次不可预测，长期递增 |

### 抖动到底防什么

假设 1000 个客户端在同一秒因为 503 全部失败：

- **无抖动**：全部在第 1 秒重试 → 1000 个请求同时到达 → 服务端刚缓过来又被拍倒 → 第二波又全失败
- **有抖动**：散到 500 毫秒里 → 每秒几百个 → 服务端能一段一段吃掉

**这叫重试风暴（retry storm）。你的重试，成了压垮它的最后一击。**

一句话记：**指数退避是对服务端说的"我晚点再来"，抖动是对其他客户端说的"咱们别一起来"。**

### 想要严格递增怎么办

```csharp
UseJitter = false,
DelayGenerator = args => new ValueTask<TimeSpan?>(
    TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber))
    + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 300))),
```

一旦给了 `DelayGenerator`，`Delay` 和 `BackoffType` 就都不生效了，它直接接管计算。

**建议保持 decorrelated**：代码少、业界主流、对慢接口更安全。但要知道有这个开关，否则看到别人的日志是严格递增，会以为哪里配错了。

## 七、装饰器的层级：谁包谁

```
AskAsync / 工具循环某一轮
  └ pipeline.ExecuteAsync              ← Polly 重试
       └ _rawClient.GetResponseAsync         （就是 FlakyChatClient）
            └ [flaky] _calls++ → 抛 429
  └ 捕获 → 等 1.4s
  └ pipeline.ExecuteAsync              ← 第 2 次
       └ [flaky] _calls=2 → 抛 429
  └ 捕获 → 等 0.8s
  └ pipeline.ExecuteAsync              ← 第 3 次
       └ [flaky] _calls=3 → 放行 → 真 client
```

**Polly 每重试一次，就重新进一次 flaky，计数跟着涨。** 这就是 `failTimes: 2` 刚好在第 3 次成功的原因。

**反例（做反了会怎样）**：如果把 flaky 包在 Polly 外面，它一抛异常，**Polly 压根没被执行过**，异常直接冒到调用方，重试永远不会发生。

> 判断方法：**越"假"的组件越靠内，越"治理型"的组件越靠外**。
> flaky 是模拟故障的，靠内；重试是处理故障的，靠外。

## 八、三条路径的保护状态不一致（今天发现的真问题）

`LlmService` 里现在有三个出口：

| 方法 | 用哪个 client | 有 Polly 吗 |
|---|---|---|
| `AskAsync` | `_rawClient` | ✅ |
| `AskWithToolsAsync` | `_rawClient` | ✅ |
| **`AskStreamAsync`** | `_chatClient` | **❌ 完全没有** |

**流式路径没有任何重试保护。** 一旦服务端 429，流式直接抛异常，整个对话中断。

这和 W02 D6 那次「流式没有 System Prompt」是**同一个模式**：一个服务里多条路径，改造时只改了一条。不报错、不崩溃，只是行为悄悄不一致。

**为什么会漏**：流式是 `IAsyncEnumerable`，重试要包住整个枚举过程，写法和 `ExecuteAsync` 不一样。它属于"知道该做但今天没做"的范畴。

## 九、GetService 必须转发（装饰器的默认责任）

`GetService` 不是业务方法，是个"取用其他服务的通道"。`IChatClient` 只有两个业务方法，但底层 provider 手里还有模型名、Endpoint、原生 SDK 对象——这些没法写进通用接口，只能靠 `GetService(Type)` 按需取。

`LlmService.ToReply` 里这行靠它：

```csharp
var modelInfo = _chatClient.GetService<ChatClientMetadata>();
```

**装饰器是新对象，它的 `GetService` 不会自动转发给 inner。** 不写转发就是 `null`，然后：

```csharp
ModelType = info?.ProviderName,      // null
ModeId    = info?.DefaultModelId     // null
```

**这个 bug 三重掩盖**：

1. `GetService` 返回 `null` 是合法值，不抛异常
2. `info?.ProviderName` 用了 `?.`，null 安静流下去
3. CLI 没打印这三个字段，屏幕上什么都看不出来

> **`?.` 救了程序不崩，也顺手把问题藏了。这是它永远的代价。**

**正确做法**：继承 `DelegatingChatClient`，它的非业务成员全部自动转发，只需重写 `GetResponseAsync`。

**记住一条**：以后每写一个装饰器，`GetService` 和 `Dispose` 是两个默认要透传的地方，除非你明确想拦住它。

## 十、LLM 场景的三个特殊性（Polly 文档不会讲）

**① 调用本身就要 2~20 秒，重试等待必须重算**

HTTP 接口 200ms 返回，加 7 秒等待无所谓。W1 测过：qwen3 思考模式单次 19.7 秒。再加 `1+2+4=7` 秒退避，用户等 27 秒。

所以 LLM 场景的 `Delay` 要更小、`MaxRetryAttempts` 更少，并且真正该有约束的是**总时长**而不是次数。

**② 重试要花钱，超时那种最坑**

普通 HTTP 重试失败不花钱。LLM 不一样：**如果服务端已经生成完、只是响应在路上丢了，这笔钱已经花了**，重试是第二笔。而且输入 token（上千）每次都要重付。

**③ 流式几乎没法重试**

已经吐了 500 字断线，重试就是从头发一遍，用户会看到内容跳回开头。

## 十一、实测数据

`failTimes: 2`，429 限流，qwen3:8b（思考模式开着）：

| 项 | 值 |
|---|---|
| 重试等待 | 1.4s + 0.8s = 2.2s |
| 总耗时 | 16580 ms |
| 真实调用时间 | ≈ 14380 ms（两次调用，每次约 7s） |
| token | 输入 1093 / 输出 66 |
| 等价云端成本 | 0.000854 元 |
| 工具调用 | step1 → `GetOrder`，step2 → 给出回答 |

**注意重试等待只占 2.2 秒，剩下全是真实调用时间。**

因为 flaky 是"瞬间失败"，所以重试很便宜。**真实故障不是这样**——60 秒才超时的那种，重试 3 次就是 3 分钟往上。这就是"超时要包在重试内层还是外层"成为问题的原因。

## 待补（自己写）

> 你以前处理过的系统里，有没有哪次是"重试把对方打挂了"，或者反过来"该重试的地方没重试，用户直接看到报错"？
>
> 那次最后是怎么定位的？如果当时有 `[retry n]` 这种日志，会不会快很多？

这个不用查资料，你的经验里一定有。**重试是最容易被随手加上、也最容易加上去的几行就变成灾难的东西。**

## 遗留

- [ ] **`Program.cs` 里 `FlakyChatClient` 还在生效**（`failTimes: 2`），提交前要改回 `new LlmService(realChat, cost)`
- [ ] `failTimes: 99` 的场景没跑，没量出"重试用完"的总耗时
- [ ] **`AskStreamAsync` 没有 Polly 保护**（第八节）
- [ ] 没有总时长上限，只有次数上限
- [ ] 超时重试的重复计费没处理
- [ ] 流式重试没方案（第十节③）
- [ ] `GetService` 透传没有实测验证（改 `=> null` 跑一次就知道）
