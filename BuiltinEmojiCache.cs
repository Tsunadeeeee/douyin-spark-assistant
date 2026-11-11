using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// Only public built-in emoji codes and thumbnail URLs, never account state.
internal sealed class BuiltinEmojiCache
{
    internal readonly string FilePath;
    internal BuiltinEmojiCache(string folder) { FilePath=Path.Combine(folder,"builtin-emoji-cache.json"); }
    private static Dictionary<string,object> Normalize(Dictionary<string,object> item)
    {
        if(item==null||Json.Str(item,"kind")!="builtin")return null;
        string token=Json.Str(item,"token");if(String.IsNullOrEmpty(token))token=Json.Str(item,"name");
        if(!Regex.IsMatch(token,@"^\[[^\[\]\r\n]{1,40}\]$")||String.IsNullOrWhiteSpace(token.Substring(1,token.Length-2)))return null;
        string src=Json.Str(item,"src");Uri uri;
        if(src.Length>2048||!Uri.TryCreate(src,UriKind.Absolute,out uri)||uri.Scheme!="https"||!String.IsNullOrEmpty(uri.UserInfo))return null;
        return new Dictionary<string,object>{{"kind","builtin"},{"token",token},{"name",token},{"src",src},{"key",src}};
    }
    internal List<Dictionary<string,object>> Read()
    {
        var result=new List<Dictionary<string,object>>();if(!File.Exists(FilePath))return result;
        if(new FileInfo(FilePath).Length>4*1024*1024)throw new InvalidDataException("表情缓存超过大小限制");
        var document=Json.Object(File.ReadAllText(FilePath,Encoding.UTF8));
        if(Json.Str(document,"version")!="1"||!document.ContainsKey("items"))throw new InvalidDataException("表情缓存格式不正确");
        IEnumerable items=document["items"] as IEnumerable;
        if(items==null||document["items"] is string)throw new InvalidDataException("表情条目格式不正确");
        var tokens=new HashSet<string>(StringComparer.Ordinal);int count=0;
        foreach(object raw in items) {
            if(++count>5000)throw new InvalidDataException("表情缓存条目过多");
            var item=Normalize(raw as Dictionary<string,object>);
            if(item==null)throw new InvalidDataException("表情缓存包含无效条目，未覆盖原文件");
            if(tokens.Add(Json.Str(item,"token")))result.Add(item);
        }
        return result;
    }
    internal List<Dictionary<string,object>> Merge(IEnumerable<Dictionary<string,object>> incoming,out int added)
    {
        var result=Read();var indices=new Dictionary<string,int>(StringComparer.Ordinal);
        for(int i=0;i<result.Count;i++)indices[Json.Str(result[i],"token")]=i;
        added=0;bool changed=false;
        foreach(var raw in incoming) {
            var item=Normalize(raw);if(item==null)continue;string token=Json.Str(item,"token");int index;
            if(indices.TryGetValue(token,out index)) {
                if(Json.Str(result[index],"src")!=Json.Str(item,"src")){result[index]=item;changed=true;}
            } else {
                if(result.Count>=5000)throw new InvalidDataException("表情缓存条目过多");
                indices[token]=result.Count;result.Add(item);added++;changed=true;
            }
        }
        if(!changed)return result;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var document=new Dictionary<string,object>{{"version",1},{"updatedAt",DateTimeOffset.Now.ToString("o")},{"items",result}};
        string text=Json.Encode(document);if(text.Length>2*1024*1024)throw new InvalidDataException("表情缓存超过大小限制，原文件保留");string temporary=FilePath+".tmp";
        try {
            byte[] bytes=new UTF8Encoding(false).GetBytes(text);
            using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
            if(File.Exists(FilePath))File.Replace(temporary,FilePath,FilePath+".bak");else File.Move(temporary,FilePath);
        } finally { if(File.Exists(temporary))File.Delete(temporary); }
        return Read();
    }
}
