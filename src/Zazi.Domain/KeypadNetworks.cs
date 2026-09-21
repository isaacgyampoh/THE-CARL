namespace Zazi.Domain;

/// <summary>
/// A network guessed from a Ghanaian mobile number's prefix.
/// </summary>
/// <remarks>
/// Only ever a default. Numbers have been portable between networks since 2011, so a 024
/// number may well be on Telecel; this is used where a keypad agent's text command does not
/// name a network, and every reply says which network was used so a wrong guess is seen.
/// </remarks>
public static class KeypadNetworks
{
    public static string FromPrefix(string? number)
    {
        var n = GhanaPhoneNumber.Normalise(number);
        return n?[..3] switch
        {
            "024" or "025" or "053" or "054" or "055" or "059" => Networks.Mtn,
            "020" or "050" => Networks.Telecel,
            "026" or "027" or "056" or "057" => Networks.AirtelTigo,
            _ => Networks.Unspecified
        };
    }
}
