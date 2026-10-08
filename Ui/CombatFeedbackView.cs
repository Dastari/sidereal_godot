using System;
using System.Linq;
using Godot;
using Sidereal.Native;
using Sidereal.Bindings;

namespace Sidereal.Ui;

/// <summary>Accepted impact/vitals presentation; never predicts damage or requests respawn.</summary>
public partial class CombatFeedbackView : Control
{
    private readonly ClientCore core;
    private readonly Func<bool> inWorld;
    private readonly ColorRect edge;
    private readonly ShaderMaterial material;
    private readonly Label hitLabel, hurtLabel, countdown;
    private readonly PanelContainer deathPanel;
    private string actor="";
    private ulong epoch,lastShot,lastHit;
    private bool primed;
    private double shotAt=double.NegativeInfinity,hurtAt=double.NegativeInfinity;
    private CombatImpactStatus? shot;
    private double hurt;
    public CombatFeedbackView(ClientCore core,Func<bool> inWorld)
    {
        this.core=core;this.inWorld=inWorld;MouseFilter=MouseFilterEnum.Ignore;ZIndex=105;
        var shader=new Shader {Code="""
            shader_type canvas_item;
            render_mode unshaded;
            uniform vec4 damage_color : source_color;
            uniform float hurt_alpha = 0.0;
            uniform bool dead = false;
            void fragment() {
                float radial=length(UV*2.0-1.0);
                float flash=smoothstep(0.35,1.1,radial)*hurt_alpha;
                vec4 shade=vec4(0.08,0.0,0.03,mix(0.25,0.72,smoothstep(0.15,1.0,radial)));
                COLOR=dead ? shade : vec4(damage_color.rgb,flash);
            }
            """};
        material=new ShaderMaterial {Shader=shader};edge=new ColorRect {Material=material,MouseFilter=MouseFilterEnum.Ignore};AddChild(edge);
        hitLabel=UiKit.Label("",20);hitLabel.MouseFilter=MouseFilterEnum.Ignore;hitLabel.HorizontalAlignment=HorizontalAlignment.Center;AddChild(hitLabel);
        hurtLabel=UiKit.Label("",18);hurtLabel.MouseFilter=MouseFilterEnum.Ignore;AddChild(hurtLabel);
        var body=new VBoxContainer();body.AddThemeConstantOverride("separation",4);
        var title=UiKit.Heading("You died",30);title.MouseFilter=MouseFilterEnum.Ignore;body.AddChild(title);
        countdown=UiKit.Label("",15);countdown.AutowrapMode=TextServer.AutowrapMode.WordSmart;countdown.MouseFilter=MouseFilterEnum.Ignore;body.AddChild(countdown);
        var note=UiKit.Label("Your inventory is safe. Nothing was dropped.",12);note.MouseFilter=MouseFilterEnum.Ignore;note.ThemeTypeVariation="MutedLabel";note.AutowrapMode=TextServer.AutowrapMode.WordSmart;body.AddChild(note);
        deathPanel=UiKit.Panel(body,0,"FramePanel");deathPanel.MouseFilter=MouseFilterEnum.Ignore;AddChild(deathPanel);
        title.ThemeTypeVariation="DangerHeading";Hide();
    }
    public object Facts()=>new {primed,lastShot,lastHit,deathVisible=deathPanel.IsVisibleInTree(),countdown=countdown.Text,hitVisible=hitLabel.IsVisibleInTree(),hitText=hitLabel.Text};
    public override void _Process(double delta)
    {
        var connected=inWorld()&&core.Character?.Connected==true&&core.SpatialReady;
        Visible=connected;
        if(!connected){primed=false;actor="";shot=null;shotAt=hurtAt=double.NegativeInfinity;return;}
        var id=core.Character!.Id;
        var impact=core.Connection?.Db.OwnCombatImpact.Iter().FirstOrDefault(i=>i.CharacterId==id);
        var vitals=core.Vitals;
        if(vitals?.CharacterId!=id)vitals=null;
        var now=Time.GetTicksMsec()/1000d;
        if(!primed||actor!=id||epoch!=core.SharedWorldEpoch)
        {primed=true;actor=id;epoch=core.SharedWorldEpoch;lastShot=impact?.ShotSequence??0;lastHit=vitals?.HitSequence??0;shot=null;shotAt=hurtAt=double.NegativeInfinity;}
        if(impact!=null&&impact.ShotSequence!=lastShot)
        {
            if(double.IsFinite(impact.Damage)&&impact.Damage>=0&&(impact.Damage>0||impact.Kind is "object" or "character"))
            {shot=impact;shotAt=now;hitLabel.Text=HitText(impact);}
            lastShot=impact.ShotSequence;
        }
        if(vitals!=null&&vitals.HitSequence!=lastHit)
        {if(vitals.HitSequence>lastHit&&double.IsFinite(vitals.LastHitDamage)){hurtAt=now;hurt=Math.Max(0,vitals.LastHitDamage);}lastHit=vitals.HitSequence;}
        var p=SiderealPalette.Current;material.SetShaderParameter("damage_color",p.Danger);
        var hurtK=(now-hurtAt)/.45;var shotK=(now-shotAt)/1.3;
        material.SetShaderParameter("hurt_alpha",(float)(NativePreferences.Current.Snapshot.ReducedMotion?0:Math.Max(0,.55*(1-hurtK))));
        var dead=vitals?.State=="dead";material.SetShaderParameter("dead",dead);
        edge.Visible=dead||hurtK<1;
        Size=GetParent<Control>().Size;edge.Size=Size;
        hurtLabel.Visible=hurt>0&&hurtK<1;hurtLabel.Text=$"-{Math.Round(hurt,MidpointRounding.AwayFromZero):0} health";hurtLabel.Modulate=p.Danger with {A=(float)Math.Clamp(1-hurtK,0,1)};hurtLabel.Position=new Vector2(28,Math.Max(70,Size.Y-214));
        hitLabel.Visible=shot!=null&&shotK<1;hitLabel.Modulate=(shot?.Damage>0?p.Legendary:p.Muted) with {A=(float)Math.Clamp(2.2*(1-shotK),0,1)};
        hitLabel.Position=new Vector2(16,Math.Max(60,Size.Y-214-(NativePreferences.Current.Snapshot.ReducedMotion?0:(float)shotK*34)));hitLabel.Size=new Vector2(Math.Max(1,Size.X-32),28);
        deathPanel.Visible=dead;
        if(dead&&vitals!=null)
        {
            var seconds=Math.Max(0,Math.Ceiling((vitals.DownedUntilMicros-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*1000d)/1e6));
            var ownAccess=core.Connection?.Db.OwnGameShipAccess.Iter().Any(a=>a.CharacterId==id)==true;
            var where=ownAccess&&core.Ship!=null?" aboard "+core.Ship.Name:"";
            countdown.Text=seconds>0?$"Respawning{where} in {seconds:0} s":$"Respawning{where}…";
            deathPanel.Size=new Vector2(Math.Max(1,Math.Min(420,Size.X-32)),96);deathPanel.Position=new Vector2((Size.X-deathPanel.Size.X)/2,Size.Y*.18f);
        }
    }
    private string HitText(CombatImpactStatus hit)
    {
        var label=hit.Kind switch {"character"=>"Crewmate","wall"=>"Wall","glass"=>"Glass","hull"=>"Hull","hatch"=>"Hatch","ship"=>"Ship hull",_=>"Target"};
        if(hit.Kind=="object")label=core.Connection?.Db.OwnInteractions.Iter().FirstOrDefault(o=>o.PlacementId==hit.TargetId)?.Name??core.Inventory.Containers.FirstOrDefault(c=>c.PlacementId==hit.TargetId)?.Name??label;
        var amount=Math.Round(hit.Damage,MidpointRounding.AwayFromZero);var head=amount>0?$"-{amount:0}":"No damage";
        var state=hit.TargetState.Length>0?char.ToUpperInvariant(hit.TargetState[0])+hit.TargetState[1..]:"";
        var detail=hit.TargetState=="dead"?$"{label} killed":double.IsFinite(hit.TargetHp)&&double.IsFinite(hit.TargetMaxHp)&&hit.TargetMaxHp>0?$"{label} · {state} {Math.Ceiling(hit.TargetHp):0}/{hit.TargetMaxHp:0}":label;
        return head+"  "+detail;
    }
}
