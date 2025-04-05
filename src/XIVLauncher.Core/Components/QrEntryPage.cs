using System.Numerics;
using ImGuiNET;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace XIVLauncher.Core.Components;

public class QrEntryPage : Page
{
    private string otp = string.Empty;
    // private bool show = false;


    public bool Cancelled { get; private set; }

    private TextureWrap qrImage;

    public QrEntryPage(LauncherApp app)
        : base(app)
    {
    }

    public void CloseDialog()
    {
        App.State = LauncherApp.LauncherState.Main;
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 7f);

        var childSize = new Vector2(300, 250);
        var vpSize = ImGuiHelpers.ViewportSize;

        ImGui.SetNextWindowPos(new Vector2(vpSize.X / 2 - childSize.X / 2, vpSize.Y / 2 - childSize.Y / 2), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.4f);

        if (ImGui.BeginChild("###qr", childSize, true, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar))
        {
            // center text in window
            ImGuiHelpers.CenteredText("请扫描二维码");

            if (this.App.qrBytes != null)
            {
                ImGui.Dummy(new Vector2(10, 10));
                using var inputStream = new MemoryStream(this.App.qrBytes);
                using var image = Image.Load(inputStream);

                int newWidth = (int)(image.Width * 1.5);
                int newHeight = (int)(image.Height * 1.5);

                image.Mutate(x => x.Resize(newWidth, newHeight));
                
                using var outputStream = new MemoryStream();
                image.Save(outputStream, new PngEncoder());

                qrImage = TextureWrap.Load(outputStream.ToArray());
                var bPos = ImGui.GetWindowPos();
                var posX = (ImGui.GetWindowSize().X - qrImage.Size.X) * 0.5f;
                var posY = ImGui.GetCursorPosY();
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(new Vector2(bPos.X + posX, bPos.Y + posY ),
                    new Vector2(bPos.X + posX + qrImage.Size.X , bPos.Y + posY + qrImage.Size.Y ), 0xffffffff);
                ImGui.SetCursorPosX(posX);
                ImGui.Image(qrImage.ImGuiHandle, qrImage.Size);
                ImGui.Dummy(new Vector2(10, 10));
            }

            if (ImGui.Button("取消###qrCancel"))
            {
                Cancelled = true;
                // CloseDialog();
            }
        }

        ImGui.EndChild();

        ImGui.PopStyleVar();

        base.Draw();
    }
}
