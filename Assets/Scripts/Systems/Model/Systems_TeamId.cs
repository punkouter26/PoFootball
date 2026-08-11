namespace PoFootball.Models
{
    /// <summary>
    /// Which of the two competing teams a possession, a point, or a stat belongs
    /// to.
    ///
    /// This is deliberately NOT the same axis as <see cref="Systems_TeamSide"/>.
    /// Systems_TeamSide is a property of a body — slot 8 is a quarterback and is
    /// always on the offense. Systems_TeamId is a property of a *possession*: it
    /// says whose drive is being played right now.
    ///
    /// The simulation only ever fields eleven offensive bodies and eleven
    /// defensive ones, so the same offense unit plays every drive for both teams
    /// and the field is mirrored when the ball changes hands (see
    /// Systems_GameFlowSystem). The offense therefore always attacks +Y and no
    /// policy ever sees an observation frame it was not fitted against.
    /// </summary>
    public enum Systems_TeamId
    {
        Home = 0,
        Away = 1
    }

    public static class Systems_TeamIdExtensions
    {
        public static Systems_TeamId Opponent(this Systems_TeamId team)
        {
            return team == Systems_TeamId.Home ? Systems_TeamId.Away : Systems_TeamId.Home;
        }
    }
}
