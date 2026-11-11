namespace HelloLlm
{
    /// <summary>
    /// 从一段文字里抽出来的订单信息。
    ///
    /// 为什么用 record 而不是 class：
    /// 1. 一行就写完，括号里是「位置参数」，声明字段的同时就是构造函数
    /// 2. ToString 自动打印所有字段值 —— Console.WriteLine(order) 直接看到内容
    /// 3. 默认按「值」比较，两个内容相同的对象就相等（测试断言方便）
    ///
    /// 字段名就是你和模型之间的约定：越常用、越直白，模型命中率越高。
    /// </summary>
    public sealed record ParsedOrder(string OrderNo, string Status, decimal Amount);
}
