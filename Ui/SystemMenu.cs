using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Sidereal.Native;

namespace Sidereal.Ui;

/// <summary>The native adapter for the browser's six-tab system menu.</summary>
public partial class SystemMenu : DockWindow
{
    private readonly ClientCore core;
    private readonly Dictionary<string, VBoxContainer> panels = new();
    private readonly Dictionary<string, Button> tabs = new();
    private readonly List<Action> bindings = new();
    private readonly VBoxContainer rail = new();
    private VBoxContainer engines = null!, transfers = null!;
    private Label vessel = null!, power = null!, crew = null!, account = null!, graphics = null!, error = null!;
    private OptionButton vista = null!;
    private LineEdit targetAccount = null!, vesselName = null!; private Button requestTransfer = null!, renameVessel = null!;
    private string engineKey = "", transferKey = "", appearanceSource = "";
    private JsonObject appearance = new();
    private readonly List<(string Key, OptionButton Choice)> appearanceChoices = new();
    private readonly List<(string Key, ColorPickerButton Choice)> appearanceColors = new();
    private bool synchronizing;
    private bool appearanceNeedsRefresh;
    public Button InventoryButton { get; }
    public Button CharacterButton { get; }
    public Button ReturnButton { get; }
    public Button SignOutButton { get; }
    public string SelectedTab { get; private set; } = "Controls";
    public Action<Key>? GameplayAction { get; set; }
    public Action? ResetCamera { get; set; }
    public string EffectiveGraphics { get; set; } = "Antialiasing settings apply to the 3D scene.";

    public SystemMenu(ClientCore core, Action inventory, Action character, Action returnToCharacters,
        Action signOut, Action theme, Action resetLayout) : base("system-menu", "System menu", new Vector2(310, 110), new Vector2(660, 560))
    {
        this.core = core;
        foreach (var name in new[] { "Controls", "Display", "Graphics", "Vessel", "Crew", "Account" })
        {
            var captured = name; var tab = ActionButton(name, () => SelectTab(captured));
            tab.CustomMinimumSize = new Vector2(110, 40); tabs[name] = tab; rail.AddChild(tab);
            var panel = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeConstantOverride("separation", 12); panels[name] = panel; Content.AddChild(panel);
            panel.AddChild(UiKit.Heading(name, 26));
        }
        var controls = panels["Controls"];
        InventoryButton = ActionButton("I   Inventory", () => { Hide(); inventory(); }); controls.AddChild(InventoryButton);
        CharacterButton = ActionButton("C   Character", () => { Hide(); character(); }); controls.AddChild(CharacterButton);
        foreach (var (key, label) in new[] { (Key.N, "N   Navigation"), (Key.V, "V   Combat mode"), (Key.Tab, "Tab   Deck / flight view"), (Key.Z,"Z   Loot labels") })
        {
            var captured = key; var button = ActionButton(label, () => { Hide(); GameplayAction?.Invoke(captured); }); controls.AddChild(button);
            bindings.Add(() => button.Disabled = GameplayAction == null || core.Character == null);
        }
        controls.AddChild(new HSeparator());
        foreach (var line in new[] { "WASD   Walk relative to the camera · Shift sprint", "E   Use · control seat · leave seat",
            "W / S thrust · A / D turn · X cruise", "Mouse aim · Left click fire · R reload", "1–5   Assigned items · 9 / 0 quick inspection",
            "Right-drag orbit · Wheel zoom", "B   Rescue beacon · X suit hold / free", "Escape   Cancel / close / menu", "F3   Graphics · F6 interface focus" })
            controls.AddChild(Body(line, 14));
        var resetCamera = ActionButton("Reset camera", () => ResetCamera?.Invoke()); controls.AddChild(resetCamera);
        bindings.Add(() => resetCamera.Disabled = ResetCamera == null);

        var display = panels["Display"];
        display.AddChild(Body("Interface preferences are saved on this computer."));
        Slider(display, "Panel opacity", .3, 1, .01, p => p.PanelOpacity, (p,v) => p with { PanelOpacity = v });
        Slider(display, "UI scale", .75, 1.5, .05, p => p.UiScale, (p,v) => p with { UiScale = v });
        Toggle(display, "Reduced motion", p => p.ReducedMotion, (p,v) => p with { ReducedMotion = v });
        Toggle(display, "Control hints", p => p.ShowControlHints, (p,v) => p with { ShowControlHints = v });
        display.AddChild(Body("Space vista", 14));
        vista = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 38) }; display.AddChild(vista);
        vista.ItemSelected += index => { if (!synchronizing) NativePreferences.Current.Set(NativePreferences.Current.Snapshot with { VistaId = vista.GetItemMetadata((int)index).AsString() }); };
        SetVistas(new[] { ("deep-space", "Deep space") });
        bindings.Add(() => { for (var i=0;i<vista.ItemCount;i++) if(vista.GetItemMetadata(i).AsString()==NativePreferences.Current.Snapshot.VistaId) vista.Select(i); });
        display.AddChild(ActionButton("Theme colours", theme));
        display.AddChild(ActionButton("Reset window layout", resetLayout));
        display.AddChild(ActionButton("Reset display preferences", NativePreferences.Current.ResetDisplay));

        var graphicPanel = panels["Graphics"];
        graphicPanel.AddChild(Body("Adjust the game image. The interface keeps its own colours."));
        Slider(graphicPanel, "Brightness", .5, 1.5, .01, p=>p.Brightness, (p,v)=>p with {Brightness=v});
        Slider(graphicPanel, "Contrast", .5, 1.5, .01, p=>p.Contrast, (p,v)=>p with {Contrast=v});
        Slider(graphicPanel, "Gamma", .5, 2, .01, p=>p.Gamma, (p,v)=>p with {Gamma=v});
        Slider(graphicPanel, "Saturation", 0, 2, .01, p=>p.Saturation, (p,v)=>p with {Saturation=v});
        graphicPanel.AddChild(Body("Local cabin / equipment lights",14));
        var lights = new OptionButton { CustomMinimumSize = new Vector2(0,38), SizeFlagsHorizontal=SizeFlags.ExpandFill };
        foreach(var limit in new[]{"0","4","8","16","32","all"}) { lights.AddItem(limit=="0" ? "Off" : limit=="all" ? "All" : limit); lights.SetItemMetadata(lights.ItemCount-1,limit); }
        lights.ItemSelected += index=> { if(!synchronizing) NativePreferences.Current.Set(NativePreferences.Current.Snapshot with {LocalLightLimit=lights.GetItemMetadata((int)index).AsString()}); };
        graphicPanel.AddChild(lights); bindings.Add(()=> { for(var i=0;i<lights.ItemCount;i++) if(lights.GetItemMetadata(i).AsString()==NativePreferences.Current.Snapshot.LocalLightLimit) lights.Select(i); });
        graphicPanel.AddChild(Body("Sun, fill and planet illumination remain separate from this local-light limit.",13));
        Choice(graphicPanel,"Antialiasing",new[]{"off","msaa"},p=>p.Antialiasing,(p,v)=>p with {Antialiasing=v});
        var samples = new OptionButton { CustomMinimumSize=new Vector2(0,38),SizeFlagsHorizontal=SizeFlags.ExpandFill };
        foreach(var n in new[]{2,4,8}) {samples.AddItem(n+"× MSAA");samples.SetItemMetadata(samples.ItemCount-1,n);}
        samples.ItemSelected += i=> { if(!synchronizing) NativePreferences.Current.Set(NativePreferences.Current.Snapshot with {MsaaSamples=samples.GetItemMetadata((int)i).AsInt32()});}; graphicPanel.AddChild(samples);
        bindings.Add(()=> { var p=NativePreferences.Current.Snapshot; samples.Select(p.MsaaSamples==2?0:p.MsaaSamples==8?2:1); samples.Disabled=p.Antialiasing!="msaa"; });
        graphicPanel.AddChild(Body("This client exposes Off and MSAA. TAA, FXAA and supersampling controls are unavailable in this build.",13));
        Slider(graphicPanel,"3D render scale",.5,1,.01,p=>p.RenderScale,(p,v)=>p with {RenderScale=v});
        Toggle(graphicPanel,"Lighting",p=>p.Lighting,(p,v)=>p with {Lighting=v});
        Toggle(graphicPanel,"Shadows",p=>p.Shadows,(p,v)=>p with {Shadows=v});
        Toggle(graphicPanel,"Emissive halos",p=>p.Glow,(p,v)=>p with {Glow=v});
        graphics = Body(EffectiveGraphics,13); graphicPanel.AddChild(graphics);
        graphicPanel.AddChild(ActionButton("Reset graphics",NativePreferences.Current.ResetGraphics));

        vessel=Body("Enter the world to inspect the vessel."); panels["Vessel"].AddChild(vessel);
        vesselName=new LineEdit {PlaceholderText="Vessel name",MaxLength=40,CustomMinimumSize=new Vector2(0,36)};panels["Vessel"].AddChild(vesselName);
        renameVessel=ActionButton("Rename vessel",()=>core.RenameShip(vesselName.Text.Trim()));panels["Vessel"].AddChild(renameVessel);
        power=Body(""); panels["Vessel"].AddChild(power);
        panels["Vessel"].AddChild(Body("Ship systems",18)); engines=new VBoxContainer();panels["Vessel"].AddChild(engines);
        crew=Body("No character connected.");panels["Crew"].AddChild(crew);
        panels["Crew"].AddChild(Body("Personal appearance changes are saved by the server. Equipment remains inventory controlled.",14));
        AppearanceChoice("bodyType","Body",new[]{"male","female"});
        AppearanceChoice("hairStyle","Hair style",new[]{"none","swept","cropped","crest","scientist","bob","ponytail","bun","braids"});
        AppearanceChoice("expression","Expression",new[]{"neutral","happy","stern","sad","surprised","wink","grin","determined"});
        AppearanceChoice("faceDetail","Face detail",new[]{"none","freckles","scar","scratch","tattoo","bandage","dirt","warpaint","cyber","birthmark"});
        AppearanceChoice("facialHair","Facial hair",new[]{"none","stubble","short","full","goatee","moustache","handlebar","sideburns"});
        AppearanceChoice("faceAge","Age",new[]{"young","adult","mature","elder"});
        foreach(var (key,label,value) in new[]{("skin","Skin tone","#bc8c68"),("hair","Hair colour","#332840"),("eyes","Eye colour","#3c8492")})
        {
            var row=new HBoxContainer();row.AddChild(Body(label)); var picker=new ColorPickerButton {Color=new Color(value),EditAlpha=false,CustomMinimumSize=new Vector2(70,34)};
            appearanceColors.Add((key,picker));picker.ColorChanged += color=> {if(!synchronizing) SaveAppearanceValue(key,"#"+color.ToHtml(false));};row.AddChild(picker);panels["Crew"].AddChild(row);
        }

        account=Body("Dastari account");panels["Account"].AddChild(account);
        panels["Account"].AddChild(Body("Character transfer",18));
        targetAccount=new LineEdit {PlaceholderText="Target account identity",CustomMinimumSize=new Vector2(0,36),SizeFlagsHorizontal=SizeFlags.ExpandFill};panels["Account"].AddChild(targetAccount);
        requestTransfer=ActionButton("Request character transfer",()=>core.RequestIdentityLink(targetAccount.Text.Trim()));panels["Account"].AddChild(requestTransfer);
        panels["Account"].AddChild(Body("The receiving account must accept the request before the character changes accounts.",13));
        transfers=new VBoxContainer();panels["Account"].AddChild(transfers);
        ReturnButton=ActionButton("Return to characters",returnToCharacters); panels["Account"].AddChild(ReturnButton);
        SignOutButton=ActionButton("Sign out",signOut);panels["Account"].AddChild(SignOutButton);
        error=Body("",13);error.ThemeTypeVariation="WarningLabel";Content.AddChild(error);
        SelectTab("Controls");
    }
    public override void _Ready(){base._Ready();SetSidebar(rail,128);}
    public void SelectTab(string tab)
    {
        SelectedTab=panels.ContainsKey(tab)?tab:"Controls";
        foreach(var p in panels)p.Value.Visible=p.Key==SelectedTab;
        foreach(var t in tabs)t.Value.ThemeTypeVariation=t.Key==SelectedTab?"SelectedMenuTab":"GhostButton";
    }
    public void SetVistas((string Id,string Name)[] values)
    {
        vista.Clear();foreach(var value in values){vista.AddItem(value.Name);vista.SetItemMetadata(vista.ItemCount-1,value.Id);}

    }
    private static Label Body(string text,int size=16) => UiKit.Paragraph(text,0).Sized(size);
    private static Button ActionButton(string text,Action action)
    {var b=new Button {Text=text,CustomMinimumSize=new Vector2(0,38),AutowrapMode=TextServer.AutowrapMode.WordSmart};b.Pressed+=action;return b;}
    private void Slider(VBoxContainer panel,string label,double min,double max,double step,Func<NativePreferencesSnapshot,double> get,Func<NativePreferencesSnapshot,double,NativePreferencesSnapshot> set)
    {
        var caption=Body(label,14);panel.AddChild(caption);var slider=new HSlider {MinValue=min,MaxValue=max,Step=step,CustomMinimumSize=new Vector2(0,26),SizeFlagsHorizontal=SizeFlags.ExpandFill};panel.AddChild(slider);
        slider.ValueChanged+=value=> {if(!synchronizing)NativePreferences.Current.Set(set(NativePreferences.Current.Snapshot,value));};
        bindings.Add(()=> {var v=get(NativePreferences.Current.Snapshot);caption.Text=$"{label}   {v:P0}";slider.Value=v;});
    }
    private void Toggle(VBoxContainer panel,string label,Func<NativePreferencesSnapshot,bool> get,Func<NativePreferencesSnapshot,bool,NativePreferencesSnapshot> set)
    {
        var button=new CheckBox {Text=label,CustomMinimumSize=new Vector2(0,34)};panel.AddChild(button);
        button.Toggled+=v=> {if(!synchronizing)NativePreferences.Current.Set(set(NativePreferences.Current.Snapshot,v));};bindings.Add(()=>button.ButtonPressed=get(NativePreferences.Current.Snapshot));
    }
    private void Choice(VBoxContainer panel,string label,string[] values,Func<NativePreferencesSnapshot,string> get,Func<NativePreferencesSnapshot,string,NativePreferencesSnapshot> set)
    {
        panel.AddChild(Body(label,14));var choice=new OptionButton {CustomMinimumSize=new Vector2(0,38),SizeFlagsHorizontal=SizeFlags.ExpandFill};foreach(var v in values)choice.AddItem(v=="msaa"?"MSAA":"Off");panel.AddChild(choice);
        choice.ItemSelected+=i=>{if(!synchronizing)NativePreferences.Current.Set(set(NativePreferences.Current.Snapshot,values[(int)i]));};bindings.Add(()=>choice.Select(Math.Max(0,Array.IndexOf(values,get(NativePreferences.Current.Snapshot)))));
    }
    private void AppearanceChoice(string key,string label,string[] values)
    {
        panels["Crew"].AddChild(Body(label,14));var choice=new OptionButton {CustomMinimumSize=new Vector2(0,36),SizeFlagsHorizontal=SizeFlags.ExpandFill};
        foreach(var v in values){choice.AddItem(char.ToUpperInvariant(v[0])+v[1..]);choice.SetItemMetadata(choice.ItemCount-1,v);}appearanceChoices.Add((key,choice));
        choice.ItemSelected+=i=> {if(!synchronizing)SaveAppearanceValue(key,choice.GetItemMetadata((int)i).AsString());};panels["Crew"].AddChild(choice);
    }
    private void SaveAppearanceValue(string key,string value)
    {
        if(core.GameplayPending)return;
        var next=(JsonObject)appearance.DeepClone();next[key]=value;
        core.SaveAppearance(next.ToJsonString());appearanceNeedsRefresh=true;
    }
    public void Refresh()
    {
        synchronizing=true;foreach(var update in bindings)update();synchronizing=false;
        graphics.Text=EffectiveGraphics;
        error.Text=NativePreferences.Current.PersistenceError??SiderealPalette.Current.PersistenceError??"";error.Visible=error.Text.Length>0;
        var actor=core.Character; var ship=core.Ship;
        vesselName.Visible=renameVessel.Visible=ship!=null&&core.Instance==null;renameVessel.Disabled=core.GameplayPending||vesselName.Text.Trim().Length<2;
        vessel.Text=ship==null?"No vessel is assigned to this character.":$"{ship.Name}\nPosition {ship.X:F1}, {ship.Y:F1} m\nRevision {ship.Revision}\n"+(core.Instance!=null?"Edit this construction template's name in Shipyard.":"");
        var summary=core.Connection?.Db.OwnShipNetworks.Iter().FirstOrDefault(n=>n.ShipId==actor?.ShipId);
        power.Text=summary==null?(core.Power is {} p?$"Power {(p.Brownout?"Brownout":p.CorePowered?"Online":"Offline")}\nGeneration {p.GenerationW/1000:F1} kW · Demand {p.DemandW/1000:F1} kW":"Systems telemetry unavailable."):
            $"{summary.Status} · Server compile r{summary.CompileRevision}\nMass {summary.MassKg/1000:F2} t\nGeneration {summary.GenerationKw:F1} kW · Storage {summary.StorageKwh:F1} kWh\nCruise demand {summary.CruiseDemandKw:F1} kW · Balance {summary.CruiseBalanceKw:F1} kW\nCombat demand {summary.CombatDemandKw:F1} kW · Balance {summary.CombatBalanceKw:F1} kW\nFuel {summary.FuelLoadedL:F1} / {summary.FuelCapacityL:F1} L\nControl slots {summary.ControlSlotsUsed} / {summary.ControlSlots}";
        var fittings=core.Connection?.Db.OwnAuthoredFlightPowerFittings.Iter().Where(f=>f.ShipId==actor?.ShipId).OrderBy(f=>f.Id).ToArray()??Array.Empty<Sidereal.Bindings.AuthoredFlightPowerFitting>();
        var signature=string.Join("|",fittings.Select(f=>$"{f.Id}:{f.Powered}"))+"#"+actor?.Id+"#"+core.Flight?.Active+"#"+core.GameplayPending+"#"+core.DevicePowerAvailable+"#"+core.DevicePowerUnavailableReason;
        if(signature!=engineKey)
        {
            engineKey=signature;Clear(engines);
            if(fittings.Length==0)engines.AddChild(Body("No disclosed engine connections.",14));
            else if(!core.DevicePowerAvailable)engines.AddChild(Body(core.DevicePowerUnavailableReason,14));
            foreach(var fitting in fittings.Where(f=>f.Kind is "computer" or "actuator"))
            {
                var captured=fitting;var checkbox=new CheckBox {Text=$"{fitting.SourceDeviceId} · {(fitting.Powered?"Connected":"Disconnected")}",ButtonPressed=fitting.Powered,Disabled=!core.DevicePowerAvailable||ship==null||core.Flight?.Active!=true||core.GameplayPending,TooltipText=core.DevicePowerAvailable?"":core.DevicePowerUnavailableReason};
                checkbox.Toggled+=value=> { if(!core.DevicePowerAvailable)return;if(captured.Kind=="computer") core.SetComputerPower(captured.PlacedObjectId,value); else core.SetEnginePower(captured.PlacedObjectId,value); };engines.AddChild(checkbox);
            }
        }
        crew.Text=actor==null?"No character connected.":$"{actor.Name}\n{core.Inventory.Items.Count(i=>i.EquipmentSlot.Length>0)} equipped items";
        var ownLook=core.Appearance;var serialized=ownLook?.AppearanceJson??"{}";
        if(serialized!=appearanceSource||appearanceNeedsRefresh&&!core.GameplayPending)
        {
            appearanceNeedsRefresh=false;
            appearanceSource=serialized;try{appearance=JsonNode.Parse(serialized) as JsonObject??new();}catch(JsonException){appearance=new();}
            var resolved=CrewAssets.Catalog.Resolve(serialized,new Dictionary<string,string>());
            synchronizing=true;
            foreach(var (key,choice) in appearanceChoices){var value=resolved.Get(key);for(var i=0;i<choice.ItemCount;i++)if(choice.GetItemMetadata(i).AsString()==value)choice.Select(i);}
            foreach(var (key,choice) in appearanceColors)if(resolved.Get(key) is {} color&&Color.HtmlIsValid(color))choice.Color=new Color(color);
            synchronizing=false;
        }
        foreach(var (_,choice) in appearanceChoices)choice.Disabled=ownLook==null||core.GameplayPending;
        foreach(var (_,choice) in appearanceColors)choice.Disabled=ownLook==null||core.GameplayPending;
        var target=targetAccount.Text.Trim();if(target.StartsWith("0x",StringComparison.OrdinalIgnoreCase))target=target[2..];
        targetAccount.Visible=requestTransfer.Visible=actor!=null; requestTransfer.Disabled=actor==null||target.Length!=64||target.Any(c=>!Uri.IsHexDigit(c))||core.GameplayPending;
        account.Text=actor==null?"Dastari account\nNo character is linked to this session.":$"{actor.Name}\nThis character is saved to the connected account.";
        var links=core.Connection?.Db.OwnIdentityLinks.Iter().ToArray()??Array.Empty<Sidereal.Bindings.VisibleIdentityLink>();
        var keyLinks=string.Join("|",links.Select(l=>$"{l.Id}:{l.Status}:{l.Side}"))+"#"+actor?.Id;
        if(keyLinks!=transferKey)
        {
            transferKey=keyLinks;Clear(transfers);
            if(actor==null&&core.Connection!=null)
            {
                transfers.AddChild(Body("Character transfer account code",14));
                transfers.AddChild(new LineEdit {Text=core.Connection.Identity?.ToString()??"",Editable=false,CustomMinimumSize=new Vector2(0,36)});
                transfers.AddChild(Body("In the browser holding your development character, open Account / Character transfer and request transfer to this account. Accept the request here.",13));
            }
            foreach(var link in links)
            {
                transfers.AddChild(Body($"{link.CharacterName}: {link.Status}",14));
                if(link.Side=="target"&&link.Status=="pending"&&actor==null){var id=link.Id;transfers.AddChild(ActionButton("Accept character transfer",()=>core.AcceptIdentityLink(id)));}
            }
        }
    }
    private static void Clear(Node container){foreach(var node in container.GetChildren()){container.RemoveChild(node);node.QueueFree();}}
}

internal static class MenuLabelExtensions
{
    public static Label Sized(this Label label,int size){label.AddThemeFontSizeOverride("font_size",size);label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;return label;}
}
