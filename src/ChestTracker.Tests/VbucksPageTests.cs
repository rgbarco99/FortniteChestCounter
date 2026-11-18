namespace ChestTracker.Tests;

/// <summary>
/// The Save the World alerts are read off somebody else's page (see Vbucks.cs for why there is no
/// API to ask instead), so the shape of that page is the only thing that can break them, and it
/// can break them quietly: a parse that reads the right number of V-Bucks off the wrong mission
/// looks perfectly healthy from the outside. That is exactly what happened — the alerts that pay
/// are the highlighted ones, their paragraph carries a style attribute, and reading an alert as
/// "&lt;p&gt;&lt;img" swallowed each of them into the paragraph above, so 50 V-Bucks came out
/// paired with the name and the place of the mission before it.
///
/// So the page below is not a copy of theirs: it is the shapes of theirs, each one on purpose —
/// a plain alert, an alert whose reward tooltip carries paragraphs and pictures of its own, the
/// two highlighted ones that actually pay, the countdown script that closes the grid, and the
/// article underneath with a screenshot of an alert in it.
/// </summary>
public sealed class VbucksPageTests
{
    private const string STR_PAGE = """
<html><body>
<p>Welcome to the <span id="missions"><b><span id="new-missions"></span></b> alerts.</span></p>
<div class="container"><div class="row">
<div class="col-xs-12 col-sm-6 col-md-3">
<p class="jrfont" style="font-size: 20px;margin:0;padding-left:3px">STONEWOOD</p>
<p><img src="https://freethevbucks.com/wp-content/uploads/Fight_the_Storm.png" width="18" height="18" alt="Fight the Storm" data-toggle="tooltip" title="Fight the Storm - Forest"> <b>5</b><i class="icon-flash"></i> <span data-toggle='tooltip' data-html='true' title='<img src="/ingame/T-Defenders-Defender-Pistol-L.png" style="width:100%"><b>Pistol Defender (Common)</b>'>Pistol Defender (Common)</span></p>
<p><img src="https://freethevbucks.com/wp-content/uploads/Retrieve_the_Data.png" width="18" height="18" alt="Retrieve the Data" data-toggle="tooltip" title="Retrieve the Data - Autumn Industrial Park"> <b>52</b><i class="icon-flash"></i> <span data-toggle='tooltip' data-html='true' title='<img src="/ingame/T-Constructor-HID-Constructor-007-UC-T01-L.png" style="width:100%"><b>Controller Harper (Uncommon)</b><br><i>Always in control.</i><p><img src="/ingame/Icon-Constructor-Decoy-128.png" style="width:23%;"><img src="/ingame/Icon-Constructor-BullRush-128.png" style="width:23%;"></p><p style="background-color:#2e3d61"><b>Grease the Wheels+</b><br>Decreases <b>Cooldown</b> by <b>x%</b>.</p>'>Controller Harper (Uncommon), Pure Drop of Rain (x5)</span></p>
<p style="background-color:rgba(255, 215, 0, 0.26);padding-top: 1px;border: 1px solid yellow;border-width: 0px 0px 2px 0px;"><img src="https://freethevbucks.com/wp-content/uploads/Repair_the_Shelter.png" width="18" height="18" alt="Repair the Shelter" data-toggle="tooltip" title="Repair the Shelter - Industrial Park"> <b>52</b><i class="icon-flash"></i> <span data-toggle='tooltip' data-html='true' title='<img src="/ingame/T-Items-WeaponPerkUp-Generic-L.png" style="width:100%"><b>RE-PERK!</b>'><b style="color:#FFD700">V-Bucks</b> (x50), RE-PERK! (x75)</span></p>
</div>
<div class="col-xs-12 col-sm-6 col-md-3">
<p class="jrfont" style="font-size: 20px;margin:0;padding-left:3px">CANNY VALLEY</p>
<p style="background-color:rgba(255, 215, 0, 0.26);padding-top: 1px;"><img src="https://freethevbucks.com/wp-content/uploads/Ride_the_Lightning.png" width="18" height="18" alt="Ride the Lightning" data-toggle="tooltip" title="Ride the Lightning - Industrial Park"> <b>70</b><i class="icon-flash"></i> <span data-toggle='tooltip' data-html='true' title='<img src="/ingame/T-Items-WeaponPerkup-VR-L.png" style="width:100%"><b>Epic PERK-UP!</b>'><b style="color:#FFD700">V-Bucks</b> (x50), Epic PERK-UP! (x75)</span></p>
<p style="background-color:rgba(234, 50, 234, 0.26)"><img src="https://freethevbucks.com/wp-content/uploads/Category_3_Storm.png" width="18" height="18" alt="Category 3 Storm" data-toggle="tooltip" title="Category 3 Storm - Ghost Town"> <b>160</b><i class="icon-flash"></i> <span data-toggle='tooltip' data-html='true' title='<img src="/ingame/T-Items-Reagent-C-T03-L.png" style="width:100%"><b>Eye of the Storm</b>'>Eye of the Storm (x36)</span></p>
</div>
</div></div>
<script>var expirationDates = ["2099-01-01T00:00:00.000Z","2099-01-01T00:00:00.000Z"];</script>
<h2>What is a mission alert?</h2>
<p>Mission alerts rotate daily and some of them pay <b>V-Bucks</b> (x999).</p>
<div class="image-with-desc">
<p><img src="https://freethevbucks.com/wp-content/uploads/vbucks-mission-alert.png" width="1000" height="380" alt="A V-Bucks mission alert"> <b>52</b></p>
</div>
</body></html>
""";

    [Fact]
    public void Cada_alerta_con_pavos_sale_con_su_propia_mision()
    {
        var objView = Vbucks.Parse(STR_PAGE);

        Assert.False(objView.Failed);
        Assert.Equal(100, objView.Total);
        Assert.Equal(5, objView.Seen);          // five alerts on the page, two of them paying

        // Sorted by what they pay and then by power level, which is what the panel draws
        Assert.Collection(objView.Missions,
            objMission =>
            {
                Assert.Equal("Repair the Shelter", objMission.Mission);
                Assert.Equal(52, objMission.Power);
                Assert.Equal("Industrial Park", objMission.Area);
                Assert.Equal("Stonewood", objMission.Zone);
                Assert.Equal(50, objMission.Vbucks);
            },
            objMission =>
            {
                Assert.Equal("Ride the Lightning", objMission.Mission);
                Assert.Equal(70, objMission.Power);
                Assert.Equal("Industrial Park", objMission.Area);
                Assert.Equal("Canny Valley", objMission.Zone);
                Assert.Equal(50, objMission.Vbucks);
            });
    }

    [Fact]
    public void La_caducidad_la_pone_la_pagina_y_no_la_medianoche()
    {
        var objView = Vbucks.Parse(STR_PAGE);

        // Far in the future on purpose: only an expiry still ahead is about the alerts that are
        // running, so a date of the week this was written would have made the test rot by itself.
        Assert.Equal(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), objView.ExpiresUtc);
    }

    /// <summary>
    /// A page that reads as no alerts at all is a page that changed shape, and saying "today pays
    /// nothing" would be the one answer that cannot be told apart from the truth by looking at it.
    /// </summary>
    [Fact]
    public void Una_pagina_que_cambia_de_forma_se_declara_ilegible()
    {
        var objView = Vbucks.Parse("<html><body><p>Nothing here any more.</p></body></html>");

        Assert.True(objView.Failed);
        Assert.Empty(objView.Missions);
    }
}
