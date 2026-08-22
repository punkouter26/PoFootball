using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// The contract a player exposes to the systems layer: where the body is, what
    /// role it fills, and how to put it back on its mark. Implemented by
    /// Agent_FootballPlayer. Systems drive the simulation through this interface so
    /// they never need a reference to the Agent type itself — which keeps
    /// PoFootball.Systems free of any ML-Agents dependency.
    ///
    /// EVERYTHING HERE APPLIES IN BOTH MODES. Reinforcement-learning verbs — the
    /// terminal reward and the episode boundary — mean nothing during a played
    /// game, where the policies are only inferencing, and they used to sit on this
    /// interface anyway. They now live on <see cref="Systems_ITrainingHandle"/>,
    /// which only the training path resolves. Fatigue stays here: it is exertion
    /// derived from applied force, part of the simulation rather than of learning,
    /// and it has to be cleared on every reset in either mode.
    /// </summary>
    public interface Systems_IPlayerHandle
    {
        int Id { get; }

        Systems_PlayerRole Role { get; }

        Systems_TeamSide Side { get; }

        Vector2 Position { get; }

        Vector2 Velocity { get; }

        /// <summary>
        /// Accumulated exertion in [0, 1], where 1 is the maximum penalty the
        /// simulation applies. Exposed for presentation only — a tired player is
        /// drawn desaturated, which is the one piece of internal agent state a
        /// viewer can otherwise never see. Nothing in the systems layer reads it,
        /// and the agent remains the only writer.
        /// </summary>
        float Fatigue { get; }

        /// <summary>
        /// Whether this player currently has the ball. Presentation could derive
        /// this by comparing <see cref="Id"/> against Systems_BallModel.CarrierId,
        /// but that is only true while the ball is Held — during a pass nobody is
        /// the carrier, and a view reading the model would flicker the highlight
        /// off mid-throw. The agent already latches the real answer in SetCarrier.
        /// </summary>
        bool IsCarrier { get; }

        /// <summary>
        /// Zeroes accumulated fatigue. Must be called BEFORE ResetTo restores the
        /// body, per CLAUDE.md section 2 — otherwise episode two starts pre-tired
        /// (acceptance criterion #11).
        /// </summary>
        void ClearFatigue();

        /// <summary>Teleports the body to its formation slot and zeroes velocity.</summary>
        void ResetTo(Vector2 position);

        void SetCarrier(bool isCarrier);

        /// <summary>
        /// Stops the body dead where it stands, without moving or turning it.
        ///
        /// Called once, at the final whistle, and nowhere else. Agents already stop
        /// driving the instant the play is not Live — Agent_FootballPlayer
        /// .OnActionReceived returns early — so what was left after a game ended was
        /// pure coasting momentum bleeding off against linear damping, which read on
        /// screen as twenty-two players still wandering around underneath the
        /// final-score overlay as though nobody had told them.
        ///
        /// Deliberately NOT ResetTo(Position): that zeroes velocity too, but it also
        /// snaps every body back to its side's facing, so the last frame of the game
        /// would jump. A game should end where it was actually being played.
        /// </summary>
        void Freeze();

        /// <summary>
        /// Repaints this body's base team colour, and applies it immediately unless
        /// the player is currently carrying (the white carrier highlight wins until
        /// possession leaves it).
        ///
        /// It has to go through the handle rather than the view writing
        /// SpriteRenderer.color directly, for the same reason
        /// <see cref="SetCarrier"/> lives here: the agent caches its base colour and
        /// restores that cache every time it stops carrying. A view that painted the
        /// renderer behind the agent's back would be overwritten by the next tackle,
        /// and the team colours would drain off the field one player at a time —
        /// which is exactly the failure Systems_RoleShapeApplier's execution order
        /// was set to avoid.
        ///
        /// Never called in Training: Systems_RoleShapeApplier is only in SCN_GAME,
        /// and a training scene has no possession to follow.
        /// </summary>
        void SetTeamColor(Color color);
    }

    /// <summary>
    /// The reinforcement-learning half of a player, resolved only on the training
    /// path. Also implemented by Agent_FootballPlayer, so the same component
    /// satisfies both contracts and the scene graph is identical in either mode.
    ///
    /// Split out of <see cref="Systems_IPlayerHandle"/> because a played game never
    /// calls either method: the brains are inferencing from a frozen .onnx, a
    /// reward they cannot learn from is arithmetic nobody reads, and an "episode"
    /// is a training concept that has no counterpart in a game made of downs.
    /// Systems_EpisodeDirector no longer knows these methods exist —
    /// Systems_ITrainingEpisodeBoundary does.
    /// </summary>
    public interface Systems_ITrainingHandle
    {
        /// <summary>
        /// Applies the sparse end-of-play reward. The agent owns the reward maths
        /// (it references PoFootball.Rewards); the caller only says what happened.
        ///
        /// passCompleted is carried separately from the outcome because a catch and
        /// the tackle that ends the play are different events — a completion is not
        /// one of the Systems_PlayOutcome values and never terminates a play by
        /// itself, so without it the reward layer can only see the two ways a pass
        /// goes WRONG.
        /// </summary>
        void ApplyTerminalReward(
            Systems_PlayOutcome outcome, float netYards, bool passCompleted);

        /// <summary>Ends this agent's ML-Agents episode. Repositioning is the director's job.</summary>
        void EndEpisodeNow();
    }
}
