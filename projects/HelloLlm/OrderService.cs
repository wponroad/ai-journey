using System.ComponentModel;

namespace HelloLlm
{
    // 委托类型只是为了让 AIFunctionFactory 能推断出方法名和参数名。
    // 直接传方法组在有重载时会有歧义，显式转成委托最稳。
    public delegate string GetOrderDelegate(string orderNo);
    public delegate string GetStockDelegate(string sku);
    public delegate string CalcPriceDelegate(string sku, int qty);
    public delegate string GetLatestOrderNoDelegate();

    /// <summary>
    /// 假数据版的业务服务。
    /// 真实项目里这几个方法背后是数据库，但**签名和描述才是重点** ——
    /// 模型看不到你的实现，只能看到 [Description] 和参数类型。
    /// </summary>
    public class OrderService
    {
        private static readonly Dictionary<string, (string Status, decimal Amount)> Orders = new()
        {
            ["A20261109"] = ("已发货", 128.5m),
            ["A20261110"] = ("待付款", 99.0m),
            ["A20261111"] = ("已完成", 356.8m),
        };

        private static readonly Dictionary<string, (int Stock, decimal Price)> Products = new()
        {
            ["SKU-001"] = (100, 12.9m),
            ["SKU-002"] = (0, 45.0m),
            ["SKU-003"] = (7, 199.0m),
        };

        [Description("查询最近一笔订单的订单号。当用户说「最近那单」「上次那个订单」但没给订单号时，先用这个。")]
        public string GetLatestOrderNo()
        {
            return "A20261111";
        }

        [Description("按订单号查询订单详情，返回订单状态和金额。订单不存在时返回提示。")]
        public string GetOrder([Description("订单号，例如 A20261109")] string orderNo)
        {
            if (Orders.TryGetValue(orderNo, out var order))
                return $"订单 {orderNo}：{order.Status}，金额 {order.Amount:F2} 元";

            // 工具的错误信息也是写给模型看的 —— 它会把这句话转述给用户。
            // 返回 -1 或者直接抛异常，模型反而不知道怎么解释。
            return $"查不到订单 {orderNo}。现有示例订单号：A20261109、A20261110、A20261111。";
        }

        [Description("按商品编号查询库存数量和单价")]
        public string GetStock([Description("商品编号，例如 SKU-001")] string sku)
        {
            if (!Products.TryGetValue(sku, out var p))
                return $"没有商品 {sku}。现有示例编号：SKU-001、SKU-002、SKU-003。";

            return p.Stock > 0
                ? $"{sku} 库存 {p.Stock} 件，单价 {p.Price:F2} 元"
                : $"{sku} 已售罄，单价 {p.Price:F2} 元";
        }

        [Description("按商品编号和数量计算总价。数量超过库存时会返回库存不足。")]
        public string CalcPrice([Description("商品编号，例如 SKU-001")] string sku,
                                [Description("购买数量，必须是正整数")] int qty)
        {
            if (!Products.TryGetValue(sku, out var p))
                return $"没有商品 {sku}，无法计算价格";

            if (qty <= 0)
                return $"数量必须大于 0，你给的是 {qty}";

            if (qty > p.Stock)
                return $"{sku} 库存不足：当前只有 {p.Stock} 件，买不了 {qty} 件";

            return $"{sku} × {qty} 件 = {p.Price * qty:F2} 元（单价 {p.Price:F2} 元）";
        }
    }
}
