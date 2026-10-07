using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Buffers.Binary;
namespace Sidereal.Native;

/// <summary>Byte-for-byte port of the released straight-alpha study face compositor.</summary>
public sealed class CrewFaceAtlas
{
    private readonly JsonElement atlas;private readonly byte[] pixels;private readonly int width;
    public int Cell=>atlas.GetProperty("cell").GetInt32();
    public CrewFaceAtlas(string json,byte[] png)
    {
        using var d=JsonDocument.Parse(json);atlas=d.RootElement.Clone();
        if(png.Length<33||!png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidOperationException("Bad crew face PNG.");
        var h=0;var color=0;using var compressed=new MemoryStream();
        for(var at=8;at+12<=png.Length;)
        {
            var count=checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at)));var type=System.Text.Encoding.ASCII.GetString(png,at+4,4);if(count<0||at+12L+count>png.Length)throw new InvalidOperationException("Truncated crew face PNG.");
            if(type=="IHDR"){width=checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at+8)));h=checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at+12)));color=png[at+17];if(png[at+16]!=8||color is not(2 or 6)||png[at+20]!=0)throw new InvalidOperationException("Unsupported crew face PNG.");}
            if(type=="IDAT")compressed.Write(png,at+8,count);at+=12+count;if(type=="IEND")break;
        }
        if(width<1||h<1||width>4096||h>4096)throw new InvalidOperationException("Invalid crew atlas bounds.");compressed.Position=0;using var inflate=new ZLibStream(compressed,CompressionMode.Decompress);using var rawStream=new MemoryStream();inflate.CopyTo(rawStream);var raw=rawStream.ToArray();var bpp=color==6?4:3;var stride=width*bpp;if(raw.Length!=(stride+1)*h)throw new InvalidOperationException("Incomplete crew face pixels.");var bytes=new byte[stride*h];
        for(var y=0;y<h;y++)for(var x=0;x<stride;x++)
        {
            var a=x>=bpp?bytes[y*stride+x-bpp]:0;var b=y>0?bytes[(y-1)*stride+x]:0;var c=x>=bpp&&y>0?bytes[(y-1)*stride+x-bpp]:0;var v=(int)raw[y*(stride+1)+1+x];
            v+=raw[y*(stride+1)] switch {0=>0,1=>a,2=>b,3=>(a+b)>>1,4=>Paeth(a,b,c),_=>throw new InvalidOperationException("Invalid crew PNG filter.")};bytes[y*stride+x]=(byte)(v&255);
        }
        pixels=new byte[width*h*4];for(var i=0;i<width*h;i++){System.Array.Copy(bytes,i*bpp,pixels,i*4,bpp);if(bpp==3)pixels[i*4+3]=255;}
    }
    private static int Paeth(int a,int b,int c){var p=a+b-c;var pa=Math.Abs(p-a);var pb=Math.Abs(p-b);var pc=Math.Abs(p-c);return pa<=pb&&pa<=pc?a:pb<=pc?b:c;}
    private static byte[] Tint(string hex)
    {
        hex=hex.TrimStart('#');return hex.Length==6&&uint.TryParse(hex,System.Globalization.NumberStyles.HexNumber,null,out var n)?new[]{(byte)(n>>16),(byte)(n>>8),(byte)n}:new byte[]{255,255,255};
    }
    public byte[] Compose(CrewLook look,string? expression=null,string? blinkEyes=null)
    {
        var name=expression??look.Get("expression","neutral");if(atlas.TryGetProperty("aliases",out var aliases)&&aliases.TryGetProperty(name,out var alias))name=alias.GetString()!;
        var expressions=atlas.GetProperty("expressions");if(!expressions.TryGetProperty(name,out var ex))ex=expressions.GetProperty("neutral");
        var skin=Tint(look.Get("skin"));var hair=Tint(look.Get("hair"));var eye=Tint(look.Get("eyes"));var outBytes=new byte[Cell*Cell*4];
        for(var i=0;i<Cell*Cell;i++){outBytes[i*4]=skin[0];outBytes[i*4+1]=skin[1];outBytes[i*4+2]=skin[2];outBytes[i*4+3]=255;}
        var eyes=CrewCatalog.Text(ex,"eyes");var blinking=blinkEyes!=null&&!atlas.GetProperty("blinkSuppressedEyes").EnumerateArray().Any(x=>x.GetString()==eyes);var iris=CrewCatalog.Text(ex,"iris","open");var irisFrame=blinking||iris=="none"?"none":iris+"@"+CrewCatalog.Text(atlas.GetProperty("looks"),"0","c");
        var row=0;foreach(var l in atlas.GetProperty("layers").EnumerateArray())
        {
            var layer=l.GetString()!;var frame=layer switch{"marks"=>look.Get("faceDetail","none") is "scar"?"scars":look.Get("faceDetail","none"),"age"=>look.Get("faceAge") switch{"mature"=>"lines","elder"=>"older",_=>"none"},"iris" or "glint"=>irisFrame,"eyes"=>blinking?blinkEyes!:eyes,_=>CrewCatalog.Text(ex,layer,"none")};
            var frames=atlas.GetProperty("frames").GetProperty(layer).EnumerateArray().Select(x=>x.GetString()).ToArray();var col=System.Array.IndexOf(frames,frame);
            if(col>=0)for(var y=0;y<Cell;y++)for(var x=0;x<Cell;x++)
            {
                var source=((row*Cell+y)*width+col*Cell+x)*4;var target=(y*Cell+x)*4;if(source+3>=pixels.Length)throw new InvalidOperationException("Crew atlas frame outside texture.");var alpha=pixels[source+3]/255d;if(alpha==0)continue;
                for(var k=0;k<3;k++){var tint=layer=="iris"?eye[k]/255d:layer=="brows"?hair[k]*.6/200:1;var value=Math.Min(255,pixels[source+k]*tint);outBytes[target+k]=(byte)Math.Round(value*alpha+outBytes[target+k]*(1-alpha),MidpointRounding.ToEven);}
            }
            row++;
        }
        return outBytes;
    }
    public string? Blink(double seconds)
    {
        var phase=seconds%4.3;var offset=4.3-atlas.GetProperty("blink").EnumerateArray().Sum(x=>x.GetProperty("seconds").GetDouble());
        if(phase<offset)return null;phase-=offset;foreach(var p in atlas.GetProperty("blink").EnumerateArray()){var duration=p.GetProperty("seconds").GetDouble();if(phase<duration)return CrewCatalog.Text(p,"eyes");phase-=duration;}return null;
    }
}
