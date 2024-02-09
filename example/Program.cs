using SkiaSharp;
using WordCloud;

List<string> nordAurora = [
    "aabf616a",
    "aad08770",
    "aaebcb8b",
    "aaa3be8c",
    "aa88c0d0",
];

using var cloud = new WordCloudBuilder()
    .WithSize(1280, 720)
    .WithPadding(5)
    .WithColorFunc(text => SKColor.Parse(nordAurora[Random.Shared.Next(nordAurora.Count)]))
    .WithBackgroundImage("/your/charming/image")
    .WithBlur(5)
    .WithFontFile("/your/awesome/font")
    .Build();

var text = File.ReadAllText("./tmp.txt");
var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.All(char.IsLetter)).ToList();
var frequencies = words.GroupBy(word => word).ToDictionary(group => group.Key, group => group.Count());
using var image = cloud.GenerateImage(frequencies);
using var stream = File.OpenWrite("./wordcloud.png");
image.Encode().SaveTo(stream);
