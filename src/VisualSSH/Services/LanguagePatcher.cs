using System.IO;
using VisualSSH.Models;
using VisualSSH.Services.N64;

namespace VisualSSH.Services;

public static class LanguagePatcher
{
    public static bool Supports(GameEntry game) =>
        CartRom.IsSupportedSystem(game.System) &&
        !string.IsNullOrWhiteSpace(game.RomPath);

    public static byte[] LoadRom(string path, string? preferName = null)
    {
        var data = RomContainer.ReadRom(path, preferName);
        if (RomContainer.IsArchiveBytes(data))
            data = RomContainer.ReadRomFromBytes(data, preferName);
        try
        {
            if (N64Rom.LooksLikeN64(data) || LooksLikeSwappedN64(data))
            {
                var z64 = N64Rom.ToZ64(data);
                if (!N64Rom.LooksLikeN64(z64))
                    throw new InvalidDataException("Dit is geen N64-ROM (.z64 / .v64 / .n64).");
                return z64;
            }
            if (CartRom.LooksLikeNes(data) || CartRom.LooksLikeSnes(data))
                return data;
            throw new InvalidDataException("Geen herkenbare N64-, NES- of SNES-ROM.");
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                ex.Message + " Bestand: " + Path.GetFileName(path) +
                ", eerste bytes: " + RomContainer.FirstBytes(data) + ".");
        }
    }

    private static bool LooksLikeSwappedN64(byte[] data) =>
        data.Length >= 2 && ((data[0] == 0x37 && data[1] == 0x80) || (data[0] == 0x40 && data[1] == 0x12));

    public static List<BkTextLine> Extract(byte[] rom, Action<string>? progress = null)
    {
        if (N64Rom.LooksLikeN64(rom))
        {
            if (N64Rom.LooksLikeBanjoKazooie(rom) && BkAssetTable.LooksValid(rom))
            {
                progress?.Invoke("Banjo-Kazooie dialoogtabel gevonden…");
                return BkAssetTable.ExtractText(rom, progress);
            }

            if (Dk64Text.LooksLike(rom))
            {
                var dk = Dk64Text.Extract(rom, progress);
                if (dk.Count == 0)
                    progress?.Invoke("Donkey Kong 64 herkend, maar geen bruikbare teksttabel gevonden.");
                return dk;
            }

            if (Sm64Text.LooksLike(rom))
            {
                var sm = Sm64Text.Extract(rom, progress);
                if (sm.Count == 0)
                    progress?.Invoke("Mario 64 herkend, maar het dialoogblok is niet gevonden.");
                return sm;
            }

            if (!N64Rom.LooksLikeBanjoKazooie(rom) && BkAssetTable.LooksValid(rom))
            {
                progress?.Invoke("Banjo-Kazooie dialoogtabel gevonden…");
                var banjo = BkAssetTable.ExtractText(rom, progress);
                if (banjo.Count > 0) return banjo;
            }
        }

        return GenericN64Text.Extract(rom, progress);
    }

    public static RomBuildResult Build(byte[] rom, IReadOnlyList<BkTextLine> lines,
        Action<RomBuildProgress>? progress = null)
    {
        if (lines.Any(l => l.Codec == "dk64"))
            return Dk64Text.Apply(rom, lines, progress);
        if (lines.Any(l => l.Codec == "sm64"))
            return Sm64Text.Apply(rom, lines, progress);
        if (lines.Any(l => l.Generic || l.InPlaceBlob))
            return GenericN64Text.Apply(rom, lines, progress);
        return BkAssetTable.ApplyText(rom, lines, progress);
    }
}
