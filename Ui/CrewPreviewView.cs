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
    private Label status = null!;
    private double elapsed;
    private bool orbiting;
    private Vector2 lastPointer;
    private float rotation = -.3f;
    public bool InteractionActive => orbiting;
    public CrewPreviewView(ClientCore core,bool demo=false)
    {
        this.core=core;this.demo=demo;Stretch=true;MouseFilter=MouseFilterEnum.Stop;
        CustomMinimumSize=new Vector2(112,300);TooltipText="Drag to rotate the character preview.";
        VisibilityChanged += () => { if (!IsVisibleInTree()) CancelInteraction(); };
    }
    public override void _Ready()
    {
        viewport=new SubViewport {TransparentBg=true,OwnWorld3D=true,Size=new Vector2I(256,512),RenderTargetUpdateMode=SubViewport.UpdateMode.WhenParentVisible};AddChild(viewport);
        var root=new Node3D();viewport.AddChild(root);model=CrewPresenter.CreatePreview(core,1);root.AddChild(model);
        camera=new Camera3D {Projection=Camera3D.ProjectionType.Orthogonal,KeepAspect=Camera3D.KeepAspectEnum.Height,Size=2.8f,Position=new Vector3(2.5f,1.4f,3.8f),Current=true,CullMask=SourceLightUnits.CameraMask(1),
            Environment=new Godot.Environment {BackgroundMode=Godot.Environment.BGMode.ClearColor,AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=new Color(.85f,.91f,1),AmbientLightEnergy=.6f,TonemapMode=Godot.Environment.ToneMapper.Filmic,TonemapExposure=1}};
        root.AddChild(camera);camera.LookAt(new Vector3(0,.9f,0));
        var key=new DirectionalLight3D {RotationDegrees=new Vector3(-45,-35,0),ShadowEnabled=false};
        SourceLightUnits.Apply(key,1.4,new Color(.85f,.94f,1),1,SourceLightUnits.Crew,1);root.AddChild(key);
        var fill=new DirectionalLight3D {RotationDegrees=new Vector3(-30,140,0),ShadowEnabled=false};
        SourceLightUnits.Apply(fill,.65,new Color(.72f,.8f,1),1,SourceLightUnits.Crew,1);root.AddChild(fill);
        status = UiKit.Label("Loading character…", 12);
        status.ThemeTypeVariation = "MutedLabel"; status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center; status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        status.MouseFilter = MouseFilterEnum.Ignore; AddChild(status); status.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        GetWindow().FocusExited += CancelInteraction;
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
    private string PresentationStatus => model?.Unsupported.Length > 0 ? "error" : model?.Ready == true && model.PendingCount == 0 ? "ready" : "loading";
    public object SmokeFacts()
    {
        var rect = GetGlobalRect();
        return new { ready = model?.Ready == true, pending = model?.PendingCount ?? 0, unsupported = model?.Unsupported ?? Array.Empty<string>(),
            presentationStatus = PresentationStatus, visible = IsVisibleInTree(), demo, rotation, interacting = orbiting,
            x = rect.Position.X, y = rect.Position.Y, width = rect.Size.X, height = rect.Size.Y, cameraSize = camera?.Size ?? 0 };
    }
    public void CancelInteraction() { orbiting = false; }
    public void RotatePreview(float radians)
    {
        if (!float.IsFinite(radians)) return;
        rotation = Mathf.PosMod(rotation + radians, Mathf.Tau);
        UpdateCamera();
    }
    private void UpdateCamera()
    {
        if (camera == null) return;
        // The source rotates the model. Orbiting this camera by the inverse angle
        // preserves that direction while leaving authored model/socket transforms intact.
        var orbit = .19f - rotation; var horizontal = 5 * Mathf.Sin(1.31f);
        camera.Position = new Vector3(Mathf.Sin(orbit) * horizontal, 1.01f + 5 * Mathf.Cos(1.31f), -Mathf.Cos(orbit) * horizontal);
        camera.LookAt(new Vector3(0, 1.01f, 0));
    }
    private void FrameModel()
    {
        var aspect = Math.Max(.01f, Size.X / Math.Max(1, Size.Y));
        var halfHeight = Math.Max(1.4f, .68f / aspect);
        if (model.Visible)
        {
            var view = camera.GlobalTransform.AffineInverse();
            FitMeshes(model);
            void FitMeshes(Node node)
            {
                if (node is MeshInstance3D { Mesh: not null } mesh && mesh.IsVisibleInTree())
                {
                    var bounds = mesh.GetAabb(); var transform = view * mesh.GlobalTransform;
                    for (var x = 0; x < 2; x++) for (var y = 0; y < 2; y++) for (var z = 0; z < 2; z++)
                    {
                        var point = transform * (bounds.Position + new Vector3(x * bounds.Size.X, y * bounds.Size.Y, z * bounds.Size.Z));
                        halfHeight = Math.Max(halfHeight, Math.Max((Math.Abs(point.X) + .045f) / aspect, Math.Abs(point.Y) + .045f));
                    }
                }
                foreach (var child in node.GetChildren()) FitMeshes(child);
            }
        }
        camera.Size = halfHeight * 2;
    }
    public override void _Process(double delta)
    {
        if(!IsVisibleInTree()||model==null)return;elapsed+=Math.Min(delta,.1);
        model.SetAppearance(Appearance());model.ReducedMotion=NativePreferences.Current.Snapshot.ReducedMotion;model.Tick(CrewMotionState.Preview,elapsed);
        TooltipText=model.Unsupported.Length>0?"Character preview has unavailable parts.":!model.Ready||model.PendingCount>0?"Loading character preview…":"Drag to rotate the character preview.";
        if(!Godot.Input.IsMouseButtonPressed(MouseButton.Left))orbiting=false;
        var presentation = PresentationStatus;
        status.Visible = presentation != "ready"; status.Text = presentation == "error" ? "Character preview unavailable" : "Loading character…";
        model.Visible = presentation == "ready"; FrameModel();
        var settings=NativePreferences.Current.Snapshot;viewport.Msaa3D=settings.Antialiasing=="off"?Viewport.Msaa.Disabled:settings.MsaaSamples switch{2=>Viewport.Msaa.Msaa2X,8=>Viewport.Msaa.Msaa8X,_=>Viewport.Msaa.Msaa4X};
    }
    public override void _GuiInput(InputEvent input)
    {
        if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left} button){orbiting=button.Pressed;lastPointer=button.Position;AcceptEvent();}
        else if(input is InputEventMouseMotion motion&&orbiting){var delta=motion.Position-lastPointer;lastPointer=motion.Position;RotatePreview(delta.X*.015f);AcceptEvent();}
    }
    public override void _ExitTree() { CancelInteraction(); GetWindow().FocusExited -= CancelInteraction; }
}
