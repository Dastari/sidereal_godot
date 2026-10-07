using System;
using System.Numerics;
using Sidereal.Native.Input;

public static class InventoryInteractionTests
{
    public static void Run()
    {
        int count=0; void Check(bool x,string m){count++;if(!x)throw new Exception(m);} void Near(float a,float b,string m)=>Check(Math.Abs(a-b)<.0001,m);
        InventoryPointerSource Source(string id="gun")=>new(id,"actor/socket/pin/row","inventory",2,4,false,new(2,2,92,188),7);
        InventoryInteraction Held(){var s=new InventoryInteraction();Check(s.Arm(Source(),new(25,49),false),"arm");Check(!s.Release(true,Source().Rect),"click must not submit");Check(s.Gesture==InventoryGesture.Held,"click-held");return s;}
        foreach(var scale in new[]{.75f,1f,1.5f}){
         var s=new InventoryInteraction();s.Arm(Source(),new Vector2(25,49),false);s.Move(new Vector2(31*scale,49*scale)/scale);Check(s.Gesture==InventoryGesture.Armed,"exact6LU becomes drag");s.Move(new Vector2(31.01f*scale,49*scale)/scale);Check(s.Gesture==InventoryGesture.Dragging,"6LU threshold did not scale");Check(s.Release(false,default),"drag needs explicit candidate");s.InvalidDrop("Occupied",0,false);Check(s.Gesture==InventoryGesture.Idle,"invalid drag retained");Check(s.Paint(.05)!=null,"invalid drag returns");Check(s.Paint(.1)==null,"return glide did not finish");
        }
        {
         var s=Held();var box=s.HeldRect();Near(box.X,2,"pickup jump x");Near(box.Y,2,"pickup jump y");s.Move(new(201,145));box=s.HeldRect();Near(box.X,178,"fractional anchor x");Near(box.Y,98,"fractional anchor y");var saved=box;for(int i=0;i<4;i++)Check(s.Rotate(),"rotate not accepted");Check(s.HeldRect()==saved,"four rotations changed footprint/grab");s.InvalidDrop("Blocked",0,false);Check(s.Gesture==InventoryGesture.Held&&s.Reason=="Blocked","invalid click must remain held");s.Cancel();Check(!s.Capturing,"cancel retained input");Check(!s.TooltipsAllowed(0,new(201,145),false),"post-cancel tooltip should stay quiet");Check(!s.TooltipsAllowed(0,new(204,145),false),"3LU tooltip threshold");Check(s.TooltipsAllowed(0,new(204.01f,145),false),"tooltip quiet never releases");
        }
        {
         var s=new InventoryInteraction();s.Arm(Source(),new(25,49),false);s.Release(false,Source().Rect);Check(s.Gesture==InventoryGesture.Idle,"release outside starts hold");s.Arm(Source(),new(25,49),false);s.Cancel();s.NewPointerPress();Check(s.Arm(Source(),new(25,49),false),"fresh press after missing release refused");s.ConsumeNextRelease();s.Release(true,Source().Rect);Check(s.Gesture==InventoryGesture.Armed,"consumed release starts hold");s.Cancel(false);
        }
        var token=new InventoryPaintToken(1,Guid.NewGuid().ToString(),11,"actor");
        {
         var s=Held();s.Commit(token,new(194,2,92,188),true,0,false);Check(!s.Capturing&&s.Submission==token,"commit didn't release gesture/store token");Near(s.Paint(.025)!.Rect.X,113,"source cubic-out25ms");Near(s.Paint(.05)!.Rect.X,170,"source cubic-out50ms");Near(s.Paint(.1)!.Rect.X,194,"landing glide target");Check(s.OriginGhost("gun",1.499),"prediction origin missing");Check(s.Paint(1.5)==null&&!s.OriginGhost("gun",1.5),"1500ms cosmetic expiry");Check(s.Submission==token,"cosmetic expiry unlocks domain");s.Observe(token with{OperationId=Guid.NewGuid().ToString()},false,"wrong");Check(s.Submission==token,"wrong UUID confirmed");s.Observe(token with{SessionGeneration=12},false,"wrong");Check(s.Submission==token,"wrong session confirmed");s.Observe(token with{ActorId="other"},false,"wrong");Check(s.Submission==token,"wrong actor confirmed");s.Observe(token,true,"ACK only");Check(s.Submission==token,"pending receipt unlocks");s.Observe(token,false,"");Check(s.Submission==null,"matching terminal not reconciled");
        }
        {
         var s=Held();s.Commit(token,new(194,2,92,188),true,0,true);Near(s.Paint(0)!.Rect.X,194,"reduced motion must skip glide");s.HideCosmetics();Check(s.Paint(0)==null&&s.Submission==token,"accepted rows cosmetic clear unlocks before ACK");s.Cancel();Check(s.Submission==token&&!s.Capturing,"blur cancels domain accounting");Check(!s.TooltipsAllowed(0,new(500,500),true),"domain pending allowed tooltip");
        }
        {
         var s=Held();s.Commit(null,new(194,2,92,188),false,0,false);Near(s.Paint(.05)!.Alpha,.115f,"source fading binding50ms");Check(s.Paint(.1)==null&&s.Submission==null,"local bind creates server prediction");
        }
        {
         var s=Held();s.Commit(token,new(194,2,92,188),true,0,false);s.SkipGlide();Near(s.Paint(.025)!.Rect.X,194,"live reduced motion retained interpolation");Check(s.Submission==token,"skip glide clears domain token");
        }
        {
         var s=new InventoryInteraction();s.Arm(Source(),new(25,49),false);s.Move(new(100,100));s.Release(false,default);s.InvalidDrop("No space",0,false,new(402,202,92,188));Check(s.Paint(.1)==null,"current-origin return did not end");s.Arm(Source(),new(25,49),false);s.Move(new(100,100));s.Release(false,default);s.InvalidDrop("No space",0,false,new(402,202,92,188));var p=s.Paint(.05)!.Rect;Near(p.X,361.375f,"return didn't use current origin x");Near(p.Y,183.375f,"return didn't use current origin y");
         s.Cancel();s.NewPointerPress();s.Arm(Source(),new(25,49),false);s.Move(new(100,100));s.Release(false,default);s.InvalidDrop("Origin clipped",0,false,returnVisible:false);Check(s.Paint(.01)==null&&!s.Capturing,"clipped origin return still draws");
        }
        {
         var s=new InventoryInteraction();s.Arm(Source(),new(25,49),false);s.Move(new(100,100));s.Cancel();Check(s.ConsumedRelease,"drag cancel failed to consume stale release");Check(!s.Release(false,default)&&!s.ConsumedRelease,"cancelled drag release leaked");s.NewPointerPress();Check(s.Arm(Source(),new(25,49),false),"fresh press blocked after cancelled drag");
        }
        {
         // Godot's engine tooltip is a passive PopupPanel. Editable popups and dialogs
         // must still cancel holding, even when they are nonexclusive or unfocusable.
         Check(!InventoryInteraction.BlocksPopup(true,true,false,true,true),"passive tooltip blocks pickup");
         Check(InventoryInteraction.BlocksPopup(true,true,true,true,true),"exclusive panel bypasses modal guard");
         Check(InventoryInteraction.BlocksPopup(true,true,false,false,true),"focusable panel bypasses modal guard");
         Check(InventoryInteraction.BlocksPopup(true,true,false,true,false),"editable pointer panel bypasses modal guard");
         Check(InventoryInteraction.BlocksPopup(true,false,false,true,true),"PopupMenu/dialog bypasses modal guard");
         Check(InventoryInteraction.BlocksPopup(true,false,true,false,false),"ordinary dialog bypasses modal guard");
         Check(!InventoryInteraction.BlocksPopup(false,false,true,false,false),"hidden popup blocks pickup");
        }
        Console.WriteLine($"PASS {count} pure gesture/threshold/anchor/rotation/invalid/consumed-release/source-easing/reduced-motion/token/cosmetic-expiry/quiet assertions.");
    }
}
