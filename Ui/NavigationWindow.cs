using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sidereal.Native;

namespace Sidereal.Ui;

internal sealed record NavigationBody(string Id,string Name,string Kind,double X,double Y,double Radius);

public partial class NavigationWindow : DockWindow
{
    private readonly ClientCore core;
    private readonly Label location, detail;
    private readonly OptionButton destination;
    private readonly Button observe, clear;
    private readonly Button join, discard;
    private readonly Label joinStatus;
    private readonly NavigationRadar radar;
    private NavigationBody[] bodies = Array.Empty<NavigationBody>();
    private string selected="", observed="", listKey="";
    public event Action<string?>? ObserveRequested;
    public NavigationWindow(ClientCore core) : base("navigation","Navigation",new Vector2(310,110),new Vector2(540,570))
    {
        this.core=core;location=UiKit.Paragraph("Navigation data unavailable.",0).Sized(14);Content.AddChild(location);
        joinStatus=UiKit.Paragraph("",0).Sized(13);Content.AddChild(joinStatus);
        join=UiKit.Button("Join shared system",()=>core.JoinSharedWorld());Content.AddChild(join);
        discard=UiKit.Button("Discard saved join request",()=>core.DiscardPendingSharedJoin());Content.AddChild(discard);
        radar=new NavigationRadar();Content.AddChild(radar);
        destination=new OptionButton {CustomMinimumSize=new Vector2(0,40),SizeFlagsHorizontal=SizeFlags.ExpandFill};Content.AddChild(destination);
        destination.ItemSelected+=i=>selected=bodies[(int)i].Id;
        detail=UiKit.Paragraph("",0).Sized(14);Content.AddChild(detail);
        observe=UiKit.Button("Observe destination",()=>{observed=selected;ObserveRequested?.Invoke(selected);Hide();});Content.AddChild(observe);
        clear=UiKit.Button("Return camera to ship",()=>{observed="";ObserveRequested?.Invoke(null);Hide();});Content.AddChild(clear);
        Content.AddChild(UiKit.Paragraph("Observation moves the camera. Fly using the control seat and cruise controls.",0).Sized(12));
    }
    private NavigationBody[] ReadBodies()
    {
        var db=core.Connection?.Db;if(db==null)return Array.Empty<NavigationBody>();
        var admission=db.OwnWorldAdmission.Iter().FirstOrDefault(a=>a.CharacterId==core.Character?.Id);
        if(admission!=null)
        {
            var motion=db.VisibleBodyMotion.Iter().Where(m=>m.SystemId==admission.SystemId).ToDictionary(m=>m.BodyId);
            return db.VisibleBodyDescriptions.Iter().Where(d=>d.Kind is "planet" or "star").Where(d=>motion.ContainsKey(d.BodyId)).Select(d=>
                new NavigationBody(d.BodyId,BodyName(d.BodyId,d.Kind),d.Kind,motion[d.BodyId].X,motion[d.BodyId].Y,d.Radius)).OrderBy(b=>b.Kind=="star"?0:1).ThenBy(b=>b.Name).ToArray();
        }
        return db.OwnSpaceBodies.Iter().Where(b=>b.ShipId==core.ActiveShipId&&b.Kind is "planet" or "star").Select(b=>
            new NavigationBody(b.Id,NavigationNames.Values.GetValueOrDefault(b.Id,Title(b.Key)),b.Kind,b.X,b.Y,b.Radius)).OrderBy(b=>b.Kind=="star"?0:1).ThenBy(b=>b.Name).ToArray();
    }
    private static string BodyName(string id,string kind)=>NavigationNames.Values.TryGetValue(id,out var name)?name:Title(kind)+" "+id[..Math.Min(8,id.Length)];
    private static string Title(string value)=>CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-',' '));
    internal static string Distance(double metres)
    {
        if(!double.IsFinite(metres))return "Unavailable";var value=metres*100000;var units=new[]{"m","km","million km","billion km","trillion km"};var divisors=new[]{1000d,1e6,1000,1000};var i=0;
        while(i<divisors.Length&&Math.Abs(value)>=divisors[i]-.005)value/=divisors[i++];return value.ToString("N2",CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')+" "+units[i];
    }
    public void Refresh()
    {
        if(!Visible)return;bodies=ReadBodies();var key=string.Join("|",bodies.Select(b=>b.Id));
        var admission=core.Connection?.Db.OwnWorldAdmission.Iter().Any(a=>a.CharacterId==core.Character?.Id&&a.ShipId==core.Character?.ShipId)==true;
        join.Visible=!admission;join.Disabled=!core.CanJoinSharedWorld||core.SharedJoinPending;discard.Visible=core.SharedJoinReviewRequired;discard.Disabled=core.SharedJoinPending;
        joinStatus.Text=admission?"Your ship is in the shared system.":core.SharedJoinMessage.Length>0?core.SharedJoinMessage:"Shared-system entry required.";
        if(key!=listKey){listKey=key;destination.Clear();foreach(var entry in bodies)destination.AddItem(entry.Name+" · "+entry.Kind);if(!bodies.Any(b=>b.Id==selected))selected=bodies.FirstOrDefault()?.Id??"";var index=Array.FindIndex(bodies,b=>b.Id==selected);if(index>=0)destination.Select(index);}
        if(observed.Length>0&&!bodies.Any(b=>b.Id==observed)){observed="";ObserveRequested?.Invoke(null);}
        var ship=core.CurrentPresentedShip;var eva=core.Eva;var x=eva?.X??ship?.X??0;var y=eva?.Y??ship?.Y??0;var heading=eva?.Heading??ship?.Heading??0;
        location.Text=(eva!=null?"EVA":ship?.Name??"Vessel unavailable")+$"\nCoordinates {x:F1}, {y:F1} m · Heading {((heading*180/Math.PI)%360+360)%360:F0}°";
        var body=bodies.FirstOrDefault(b=>b.Id==selected);
        if(body==null)detail.Text="No celestial destinations are currently disclosed.";
        else{var dx=body.X-x;var dy=body.Y-y;var bearing=(Math.Atan2(-dx,dy)*180/Math.PI+360)%360;var turn=(bearing-heading*180/Math.PI+540)%360-180;
            detail.Text=$"{body.Name}\nDistance {Distance(Math.Sqrt(dx*dx+dy*dy))}\nBearing {bearing:F0}° · Turn {turn:+0;-0;0}°\nRadius {Distance(body.Radius)}";}
        observe.Disabled=body==null||core.GameplayPending;destination.Disabled=bodies.Length==0;clear.Disabled=observed.Length==0;
        radar.Update(bodies,selected,x,y,heading);
    }
}

internal partial class NavigationRadar : Control
{
    private NavigationBody[] bodies=Array.Empty<NavigationBody>();private string selected="";private double x,y,heading;
    public NavigationRadar(){CustomMinimumSize=new Vector2(200,220);SizeFlagsHorizontal=SizeFlags.ExpandFill;MouseFilter=MouseFilterEnum.Ignore;}
    public void Update(NavigationBody[] values,string id,double shipX,double shipY,double shipHeading){bodies=values;selected=id;x=shipX;y=shipY;heading=shipHeading;QueueRedraw();}
    public override void _Draw()
    {
        var p=SiderealPalette.Current;var radius=Math.Min(Size.X,Size.Y)*.42f;var center=Size*.5f;
        SciFiFrameStyle.Paint(GetCanvasItem(),new Rect2(Vector2.Zero,Size),new Color(p.Surface,p.Opacity*.7f),p.Accent.Darkened(.65f),p.Accent,8,false);
        for(var i=1;i<=3;i++)DrawArc(center,radius*i/3,0,Mathf.Tau,96,p.Accent.Darkened(.55f),1,true);
        DrawLine(center-new Vector2(radius,0),center+new Vector2(radius,0),p.Accent.Darkened(.65f),1,true);DrawLine(center-new Vector2(0,radius),center+new Vector2(0,radius),p.Accent.Darkened(.65f),1,true);
        var max=bodies.Select(b=>Math.Sqrt((b.X-x)*(b.X-x)+(b.Y-y)*(b.Y-y))).DefaultIfEmpty(1).Max();max=Math.Max(max,1);
        foreach(var body in bodies){var point=center+new Vector2((float)((body.X-x)/max*radius),(float)(-(body.Y-y)/max*radius));var color=body.Kind=="star"?p.Warning:p.Accent;
            DrawCircle(point,body.Id==selected?5:3,color,true,-1,true);if(body.Id==selected)DrawArc(point,9,0,Mathf.Tau,32,p.Text,1,true);}
        var direction=new Vector2((float)-Math.Sin(heading),(float)-Math.Cos(heading));var right=new Vector2(-direction.Y,direction.X);
        DrawColoredPolygon(new[]{center+direction*8,center-direction*5+right*4,center-direction*5-right*4},p.Text);
        DrawString(GetThemeFont("font","Label"),new Vector2(10,Size.Y-10),"System chart",HorizontalAlignment.Left,Size.X-20,11,p.Muted);
    }
}
