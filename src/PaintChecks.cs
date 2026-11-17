#if UI_QA
namespace VgnBattery;

internal static class PaintChecks
{
    // Observe native WM_PAINT clips after resizing; DrawToBitmap would repaint
    // everything itself and hide the stale-border failure being checked here.
    internal static async Task RunAsync(Action<bool,string> check)
    {
        if (!Application.MessageLoop || Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)
            throw new InvalidOperationException("Paint checks require the WinForms STA message loop.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var popup=new BatteryPopup { Opacity=0,ShowInTaskbar=false,Enabled=false };
        var area=Screen.PrimaryScreen!.WorkingArea;
        popup.Location=new Point(area.Left+24,area.Top+24);
        var popupPaints=new List<Rectangle>();
        popup.Paint+=(_,e)=>popupPaints.Add(e.ClipRectangle);
        popup.Show();
        try
        {
            await Task.Delay(50,timeout.Token);
            popup.Update();
            int oldBottom=popup.ClientSize.Height-1;
            popupPaints.Clear();
            popup.SetSettingsExpanded(true);
            popup.Update();
            await Task.Delay(50,timeout.Token);
            check(popup.ClientSize.Height>oldBottom+1,"settings expansion grows the real popup");
            check(popupPaints.Any(clip=>clip.Contains(1,oldBottom)),
                "settings expansion repaints the old window border instead of retaining a horizontal seam");
        }
        finally { popup.Hide(); }

        using var owner=new PaintOwner { Opacity=0,ShowInTaskbar=false,Enabled=false,ClientSize=new Size(400,300) };
        using var card=new SettingsCard { Bounds=new Rectangle(20,20,240,60) };
        owner.Controls.Add(card);
        var cardPaints=new List<Rectangle>();
        card.Paint+=(_,e)=>cardPaints.Add(e.ClipRectangle);
        owner.Show();
        try
        {
            await Task.Delay(50,timeout.Token);
            card.Update();
            int oldBottom=card.ClientSize.Height-1;
            cardPaints.Clear();
            card.Height+=120;
            card.Update();
            await Task.Delay(50,timeout.Token);
            check(card.ClientSize.Height>oldBottom+1,"the test card grows without resizing its owner");
            check(cardPaints.Any(clip=>clip.Contains(card.Width/2,oldBottom)),
                "a resized settings card repaints its previous rounded bottom edge");
        }
        finally { owner.Hide(); }
    }

    private sealed class PaintOwner:Form
    {
        protected override bool ShowWithoutActivation=>true;
    }
}
#endif
