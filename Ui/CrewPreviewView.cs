using System;
using System.Linq;
using Godot;
using Sidereal.Native;
using Sidereal.InventoryUi;

namespace Sidereal.Ui;

/// <summary>The same disclosed character/loadout as the world, rendered in an isolated UI viewport.</summary>
public partial class CrewPreviewView : SubViewportContainer
{
    private readonly ClientCore core;
    private readonly bool demo;
    private SubViewport viewport = null!;
    private CrewModel model = null!;
    private Camera3D camera = null!;
    private double elapsed;
    private bool orbiting;
    private float orbit=.3f;
    public CrewPreviewView(ClientCore core,bool demo=false)
    {
        this.core=core;this.demo=demo;Stretch=true;MouseFilter=MouseFilterEnum.Stop;
        CustomMinimumSize=new Vector2(112,300);TooltipText="Drag to rotate the character preview.";
    }
    public override void _Ready()
    {
        viewport=new SubViewport {TransparentBg=true,OwnWorld3D=true,Size=new Vector2I(256,512),RenderTargetUpdateMode=SubViewport.UpdateMode.WhenParentVisible};AddChild(viewport);
        var root=new Node3D();viewport.AddChild(root);model=CrewPresenter.CreatePreview(core,1);root.AddChild(model);
        camera=new Camera3D {Projection=Camera3D.ProjectionType.Orthogonal,Size=2.3f,Position=new Vector3(2.5f,1.4f,3.8f),Current=true,
            Environment=new Godot.Environment {BackgroundMode=Godot.Environment.BGMode.ClearColor,AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=new Color(.85f,.91f,1),AmbientLightEnergy=.6f,TonemapMode=Godot.Environment.ToneMapper.Filmic,TonemapExposure=1}};
        root.AddChild(camera);camera.LookAt(new Vector3(0,.9f,0));
        var key=new DirectionalLight3D {RotationDegrees=new Vector3(-45,-35,0),LightColor=new Color(.85f,.94f,1),LightEnergy=1.4f,ShadowEnabled=false};root.AddChild(key);
        root.AddChild(new DirectionalLight3D {RotationDegrees=new Vector3(-30,140,0),LightColor=new Color(.72f,.8f,1),LightEnergy=.65f,ShadowEnabled=false});
        UpdateCamera();
    }
    private CrewAppearanceView Appearance()
    {
        if(!demo)return CrewAppearanceView.FromCore(core);
        var inventory=DemoInventory.Snapshot;
        var worn=inventory.Items.Where(i=>i.EquipmentSlot.Length>0&&i.EquipmentSlot!="hand").ToDictionary(i=>i.EquipmentSlot,i=>InventoryIcons.PresentationId(i.DefinitionId));
        var held=inventory.Items.FirstOrDefault(i=>i.EquipmentSlot=="hand");
        return new CrewAppearanceView(CrewAssets.Catalog.Resolve("{}",worn,held==null?null:CrewAssets.Catalog.DefinitionHeld(InventoryIcons.PresentationId(held.DefinitionId))));
    }
    public object SmokeFacts()=>new {ready=model?.Ready==true,pending=model?.PendingCount??0,unsupported=model?.Unsupported??Array.Empty<string>(),visible=IsVisibleInTree(),demo};
    private void UpdateCamera()
    {if(camera==null)return;camera.Position=new Vector3(Mathf.Sin(orbit)*4,1.15f,-Mathf.Cos(orbit)*4);camera.LookAt(new Vector3(0,.9f,0));}
    public override void _Process(double delta)
    {
        if(!IsVisibleInTree()||model==null)return;elapsed+=Math.Min(delta,.1);
        model.SetAppearance(Appearance());model.ReducedMotion=NativePreferences.Current.Snapshot.ReducedMotion;model.Tick(CrewMotionState.Preview,elapsed);
        TooltipText=model.Unsupported.Length>0?"Character preview has unavailable parts.":!model.Ready||model.PendingCount>0?"Loading character preview…":"Drag to rotate the character preview.";
        if(!Godot.Input.IsMouseButtonPressed(MouseButton.Left))orbiting=false;
        camera.Size=Math.Max(2.3f,1.05f/Math.Max(.1f,Size.X/Math.Max(1,Size.Y)));
        var settings=NativePreferences.Current.Snapshot;viewport.Msaa3D=settings.Antialiasing=="off"?Viewport.Msaa.Disabled:settings.MsaaSamples switch{2=>Viewport.Msaa.Msaa2X,8=>Viewport.Msaa.Msaa8X,_=>Viewport.Msaa.Msaa4X};
    }
    public override void _GuiInput(InputEvent input)
    {
        if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left} button){orbiting=button.Pressed;AcceptEvent();}
        else if(input is InputEventMouseMotion motion&&orbiting){orbit+=motion.Relative.X*.012f;UpdateCamera();AcceptEvent();}
    }
}
