# W03-D5　OpenTelemetry

> 2026-11-20　目标：能报出「某次对话花了多少钱、慢在哪一步」
> 一句话结论：**先有可观测性，才知道该不该优化。没有它，所有优化都是猜。**

## 一、要解决的问题，一句话

你现在知道**总共 12891 ms**。

但你能说出「第一次模型调用花了几秒、第二次几秒」吗？说不出来。

**今天就是把总耗时拆成几段，看清每一段。**

## 二、这东西你已经用过

| 场景 | 工具 |
|---|---|
| 微服务：网关 → 订单 → 库存 → 支付，哪一跳慢 | SkyWalking / Zipkin / Application Insights |
| 单体：一个方法调了 5 层，哪层慢 | Stopwatch 分段计时 / mini-profiler |
| **LLM：一次对话背后 2 次模型 + 1 次工具 + 1 次缓存** | **今天这件事** |

**本质就是分段计时，OTel 只是把结果变成标准格式**（能被 Jaeger 认、能跨服务串联）。

埋点是 .NET 的事，不是 OTel 的事 —— `Activity` 是 `System.Diagnostics` 自带的。

## 三、三个概念

| 概念 | 是什么 |
|---|---|
| `Activity` | 一个 span：有开始、有结束的操作 |
| `ActivitySource` | 工厂，带名字（`"HelloLlm"`） |
| 父子关系 | **自动的** —— `StartActivity` 看 AsyncLocal 里有没有"当前活动"，有就当爹 |

```csharp
using var parent = src.StartActivity("chat.request");
using var child  = src.StartActivity("llm.call");     // 自动挂到 parent 下
```

`async/await` 之间不会断，因为 AsyncLocal 跟着执行上下文走。

### 一个关键机制：没订阅者就不花钱

```csharp
using var activity = _source.StartActivity("llm.call");
// 没有监听器时，activity 是 null，这行几乎是空操作
```

**这是 .NET 遥测的设计前提**：生产环境关掉导出，埋点代码留着不产生开销。所以可以放心埋。

### `using` 不能漏

漏了 span 永远不结束，耗时是错的 —— **而且是悄悄错**，不报任何异常。

## 四、命名：分类，不是标识

这行代码里 `chat.request` 和 `llm.call` 的差别**不影响父子关系**（层级靠 AsyncLocal，不靠名字），那为什么必须区分？

**① 名字就是监控界面里显示的字**

全叫 `chat.request` 的话：

```
chat.request   21.67s
chat.request    9.60s
chat.request    2.08s
chat.request    0.006s
```

**哪个是模型、哪个是工具？全靠猜。**

**② 按名字聚合、做告警**

```
llm.call      P99 = 9.2s    ← 模型服务商慢了
tool.GetOrder P99 = 3ms     ← 自己的代码，正常
chat.request  P99 = 12s     ← 整体
```

名字一样，就算不出"模型调用平均多久"。

### 硬规矩：名字低基数，变量放 tag

| 放哪 | 放什么 | 例子 |
|---|---|---|
| **名字** | 有限、固定的分类 | `llm.call`、`cache.lookup`、`tool.GetOrder` |
| **tag** | 每次都不同的值 | `llm.step: 2`、`llm.input_tokens: 580` |

```csharp
// ❌ 高基数：每个订单一个名字，跑一天几万个"操作名"，索引撑爆
_source.StartActivity($"query_order_{orderNo}");

// ✅ 名字固定，变量进 tag
var span = _source.StartActivity("tool.GetOrder");
span?.SetTag("order.id", orderNo);
```

> 和数据库一个道理：**表名是分类（`Orders`），主键是标识（`A20261109`）。**
> 你不会把所有表都叫 `Data`，也不该把所有 span 都叫 `chat.request`。

## 五、埋在哪：四个位置

| span | 位置 | 带什么 tag |
|---|---|---|
| `chat.request` | `AskWithToolsAsync` 整体 | user、dept、question |
| `cache.lookup` | 缓存查询前后 | hit、kind |
| `llm.call` | 循环里每次 `ExecuteAsync` | step、duration_ms、in/out token |
| `tool.{name}` | 每次工具执行 | tool.name |

`llm.call` 在循环里，所以**一次对话会产出多个** —— 这正是昨天看不到的东西。

层级结果：

```
chat.request
├─ cache.lookup
├─ llm.call (step 1)
│   └─ tool.GetOrder      ← 工具挂在「请求它的那一轮」下面
└─ llm.call (step 2)
```

## 六、第一个发现：缓存比模型还慢

埋完第一次跑，抓到的东西和直觉完全相反：

```
chat.request      21.67s
├─ cache.lookup     5.75s     ← 27%
├─ llm.call #1      9.60s
│   └ tool.GetOrder 0.006s
└─ llm.call #2      2.08s
```

**缓存查询 5.75 秒，而它还是「未命中」。**

原因：`bge-m3` 有 1.2GB，第一次调用要把权重加载进内存 —— **冷启动**。

> **没有 OTel，你只会看到「耗时 21673 ms」，然后猜「可能是模型慢」——猜错了。**

## 七、盲区：埋点不全，时间会消失

`21.67 - (5.75 + 9.60 + 2.08) = 4.24s` 秒**没有任何 span 覆盖**。

这 4.24 秒在 `StoreAsync` 里 —— 缓存未命中后写回，**里面又调了一次 embedding**：

```
LookupAsync → EmbedAsync(question)   ← 第 1 次
StoreAsync  → EmbedAsync(question)   ← 第 2 次，同一个输入
```

**同一句话，算了两遍。**

### 关于盲区的一个认知

**你只能看到你埋了点的地方。** 这和"日志只能看到你打了的日志"是一回事。

但 OTel 比日志强一点：**有 `chat.request` 的总时长，所以盲区可以被算出来**（总时长 − 子 span 之和）。日志里连这个都不知道。

> 盲区不可怕，可怕的是**不知道有盲区**。因为你会拿"看得见的 17 秒"当全部，去优化它，而真正的问题藏在看不见的 4 秒里。

## 八、两个优化

| 优化 | 做法 | 省了什么 |
|---|---|---|
| **预热** | 启动时先 embed 一次，把 1.2GB 模型加载进内存 | cache.lookup 5.75s → **56ms** |
| **向量复用** | `LookupAsync` 算出的向量传给 `StoreAsync` | 盲区 4.24s → **0.04s** |

```csharp
// 返回向量，让调用方复用
public async Task<(CacheHit? Hit, float[]? QueryVector)> LookupAsync(...)

// 有就复用，没有才算
var vec = vector ?? await _embed.EmbedAsync(question, ct);
```

注意精确命中时不需要向量（走 KV，压根没算），所以返回值里是 `null`。

### 结果

| 项 | 优化前 | 优化后 |
|---|---|---|
| **chat.request** | **21.67s** | **11.96s** |
| cache.lookup | 5.75s | 0.056s |
| llm.call #1 | 9.60s | 9.75s |
| llm.call #2 | 2.08s | 2.11s |
| 盲区 | 4.24s | 0.04s |

**总耗时降 45%，模型调用时间一秒没变。**

## 九、比例变了：从 54% 到 99%

这是今天最重要的一个数：

| | 模型占比 | 你的代码占比 |
|---|---|---|
| 优化前 | 11.68 / 21.67 = **54%** | 46% |
| 优化后 | 11.86 / 11.96 = **99%** | 1% |

**直接回答了「优化到哪一步为止」：**

> 模型占 99% 的时候，再优化代码，收益上限只有 1%。
> 想更快只能动模型：换小模型、关思考模式、减少工具轮次。

**这就是为什么必须先有可观测性**。没有它，你会一直盯着"总耗时 21 秒"猜，很可能去优化工具调用（0.006 秒，优化到零也省不了 0.006 秒）。

## 十、Console Exporter 的定位

每条 Activity 输出 10 行（TraceId / SpanId / ParentSpanId / 时间戳 / 资源属性），一次请求刷 60 行。

| 用途 | 适不适合 |
|---|---|
| 本地确认「span 有没有发出来」 | ✅ |
| 人看链路 | ❌ 太吵 |
| 生产 | ❌ 用 OTLP 发到 Jaeger / Tempo |

```csharp
.AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"))
```
**今天不装 Jaeger**，Console 能验证链路通就够了。

## 遗留

- [ ] 只覆盖了 `AskWithToolsAsync`，`AskAsync` 和流式都没埋点
- [ ] 没接 OTLP / Jaeger，看不到泳道图
- [ ] 没有 traceId 透传到日志（排查时无法用 traceId 串起日志和 span）
- [ ] `cache.store` 没单独埋点（现在算在盲区里，虽然只剩 0.04s）
- [ ] 预热是阻塞的（3.4 秒），真实服务应该异步预热或保活
- [ ] `llm.cost_micros` 没进 tag（成本目前只在 SQLite 里，没有和 trace 关联）
- [ ] 本次改动未提交
