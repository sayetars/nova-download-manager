using System.Text.Json;
var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var json = "[{\"host\":\"bigcdn.com\",\"max\":5},{\"host\":\"*.slow.net\",\"max\":1}]";
try
{
    var list = JsonSerializer.Deserialize<List<Entry>>(json, opts) ?? new();
    Console.WriteLine($"count={list.Count}");
    foreach (var e in list) Console.WriteLine($"Host='{e.Host}' Max={e.Max}");
}
catch (Exception ex) { Console.WriteLine($"THREW: {ex.Message}"); }
public class Entry { public string Host { get; set; } = ""; public int Max { get; set; } }
