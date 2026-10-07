using System;
using System.Linq;
using Godot;
using Sidereal.Native;
using Sidereal.Native.Input;

namespace Sidereal.Ui;

public partial class SiderealUi
{
    private NavigationWindow navigation = null!;
    private ObjectDetailsWindow objectDetails = null!;
    private HBoxContainer shipActions = null!;
    private Label shipStatus = null!, evaStatus = null!;
    private Button cruiseButton = null!;
    private Label integrityCaption = null!;
    private ProgressBar integrityBar = null!;
    private CombatFeedbackView combatFeedback = null!;
    private void BuildGameplayPanels()
    {
        combatFeedback=new CombatFeedbackView(core,()=>WorldVisible);AddChild(combatFeedback);
        navigation=new NavigationWindow(core);navigation.ObserveRequested+=id=>ObserveBodyRequested?.Invoke(id);AddChild(navigation);windows.Add(navigation);navigation.Hide();
        objectDetails=new ObjectDetailsWindow(core);AddChild(objectDetails);windows.Add(objectDetails);objectDetails.Hide();
        shipActions=new HBoxContainer();shipActions.AddThemeConstantOverride("separation",6);
        cruiseButton=UiKit.Button("Cruise",()=>{GetViewport().GuiReleaseFocus();GameplayShortcutRequested?.Invoke(Key.X);});shipActions.AddChild(cruiseButton);
        shipActions.AddChild(UiKit.Button("Systems",()=>{core.ReleaseControls();menu.SelectTab("Vessel");menu.Show();menu.BringToFront();}));
        shipActions.AddChild(UiKit.Button("Navigation",()=>Toggle(navigation)));
        foreach(var text in new[]{"No shields","No travel drive"})shipActions.AddChild(new Button {Text=text,Disabled=true,CustomMinimumSize=new Vector2(0,44),SizeFlagsHorizontal=SizeFlags.ExpandFill,AutowrapMode=TextServer.AutowrapMode.WordSmart});
        foreach(var button in shipActions.GetChildren().OfType<Button>()){button.SizeFlagsHorizontal=SizeFlags.ExpandFill;button.AddThemeFontSizeOverride("font_size",13);}
        var column=hotbar.GetParent<VBoxContainer>();column.AddChild(shipActions);shipActions.Hide();
        integrityCaption=UiKit.Label("Components unavailable",12);fullTelemetry.AddChild(integrityCaption);integrityCaption.Hide();
        integrityBar=new ProgressBar {ShowPercentage=false,CustomMinimumSize=new Vector2(0,5),MouseFilter=MouseFilterEnum.Ignore};fullTelemetry.AddChild(integrityBar);integrityBar.Hide();
        shipStatus=UiKit.Paragraph("",0).Sized(12);fullTelemetry.AddChild(shipStatus);shipStatus.Hide();
        evaStatus=UiKit.Paragraph("",0).Sized(12);fullTelemetry.AddChild(evaStatus);evaStatus.Hide();
    }
    public void ShowObjectDetails(string? placementId)
    { if(!WorldVisible)return;if(placementId!=null)core.ReleaseControls();objectDetails.Inspect(placementId); }
    public void ToggleNavigation(){if(WorldVisible)Toggle(navigation);}
    private void RefreshGameplayPanels(bool preview)
    {
        navigation.Refresh();objectDetails.Refresh();
        var flightHud=!preview&&!InteriorView&&core.Eva==null&&core.CurrentPresentedShip!=null;
        var changed=shipActions.Visible!=flightHud||shipStatus.Visible!=flightHud||evaStatus.Visible!=(core.Eva!=null);
        shipActions.Visible=shipStatus.Visible=flightHud;evaStatus.Visible=!preview&&core.Eva!=null;hotbar.Visible=!flightHud;
        integrityCaption.Visible=integrityBar.Visible=flightHud;
        var forwardSpeed=core.CurrentPresentedShip is {} acceptedShip?GameplayRules.ForwardSpeed(acceptedShip.Heading,acceptedShip.Vx,acceptedShip.Vy):0;
        actionTitle.Text=flightHud?(core.CruiseActive?$"Cruise · {forwardSpeed:F1} m/s forward · X cancels":"Ship controls") : "Action bar";
        cruiseButton.Text=core.CruiseActive?"Cancel cruise":"Cruise";cruiseButton.Disabled=!core.CruiseAvailable||!core.IsPiloting;
        if(flightHud)
        {
            hudName.Text=core.CurrentPresentedShip?.Name??"Vessel";
            healthRow.Visible=health.Visible=weaponEnergyRow.Visible=weaponEnergy.Visible=cargoRow.Visible=carry.Visible=false;
            var damage=core.Connection?.Db.OwnShipComponentDamage.Iter().ToArray()??Array.Empty<Sidereal.Bindings.ShipComponentDamageStatus>();
            var integrity=core.Instance is {} instance?ConfiguredComponentCatalog.Read(instance.Id,core.Ship?.Id==instance.Id,instance.DocumentJson,damage):null;
            integrityCaption.Text=integrity==null?"Components unavailable":$"Components {integrity.Hp:F0} / {integrity.MaximumHp:F0}";
            integrityBar.MaxValue=Math.Max(.001,integrity?.MaximumHp??1);integrityBar.Value=integrity?.Hp??0;
            var summary=core.Systems;shipStatus.Text=(integrity==null?"Status not disclosed":$"{integrity.Damaged} damaged / {integrity.Count} configured")+"\n"+$"Mass {(summary==null?"unavailable":$"{summary.MassKg/1000:F2} t")}\n"+(core.IsPiloting?"Helm occupied":"Take the helm to fly")+"\n"+(core.Flight?.FlightAdmitted==true?"Flight admitted":"Flight not admitted");
        }
        else cargoRow.Visible=true;
        if(core.Eva is {} eva)evaStatus.Text=$"{eva.Phase} · Suit {core.EvaSuit?.Mode??"unavailable"}\n"+(eva.Stranded?"Stranded · B rescue beacon":eva.ReturnEndsMicros>0?"Emergency return in progress":"X toggles suit hold / free")+"\n"+core.GameplayMessage;
        if(changed)QueueLayout();
    }
}
