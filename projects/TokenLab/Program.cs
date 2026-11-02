using SharpToken;

var encoding = GptEncoding.GetEncoding("cl100k_base");
var encoded = encoding.Encode("Hello, world!");
//Console.WriteLine(string.Join(',', encoded));
foreach (var id in encoded)
{
    Console.Write($"[{encoding.Decode(new[] { id })}]");
}
var decoded = encoding.Decode(encoded);
Console.WriteLine(decoded);

var count = encoding.CountTokens("Hello, world!");
Console.WriteLine(count);
//Console.OutputEncoding = System.Text.Encoding.UTF8;


string inputText = "Some Text <|endofprompt|>";
var allowedSpecialTokens = new HashSet<string> { "<|endofprompt|>" };

//encoded = encoding.Encode(inputText, allowedSpecialTokens);
//Console.WriteLine(string.Join(',', encoded));
//var expectedEncoded = new List<int> { 8538, 2991, 220, 100276 };
//inputText = "Some Text";
//encoding.Encode(inputText, disallowedSpecial: new HashSet<string> { "Some" });
//Console.WriteLine(string.Join(',', encoded));
string input = Console.ReadLine();
while (!string.IsNullOrEmpty(input) && input != "exit")
{
    Console.WriteLine($"输入长度：{input.Length}");
    encoded = encoding.Encode(input);
    Console.WriteLine(new string('-', 60));
    Console.WriteLine("分割的Token：");
    Console.WriteLine(new string('-', 60));
    foreach (var id in encoded)
    {
        Console.Write($"[{encoding.Decode(new[] { id })}]");
    }
    Console.WriteLine();
    Console.WriteLine(new string('-', 60));
    decoded = encoding.Decode(encoded);
    Console.WriteLine(decoded);
    count = encoding.CountTokens(input);
    Console.WriteLine($"分割的Token数：{count}");
    input = Console.ReadLine();
}
