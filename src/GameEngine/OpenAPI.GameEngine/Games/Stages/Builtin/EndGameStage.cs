using System;
using MiNET;
using MiNET.Utils;
using MiNET.Worlds;

namespace OpenAPI.GameEngine.Games.Stages.Builtin
{
	public class EndGameStage : GameStage
	{
		/// <inheritdoc />
		public EndGameStage(Game game) : base(game, "EndGame")
		{
			
		}

		/// <inheritdoc />
		protected override void OnTick()
		{
			base.OnTick();
			
			if (StageTicks % 20 == 0)
			{
				Game.Level.BroadcastMessage($"{ChatColors.Gold}The game has finished.", MessageType.Popup);
			}
		}

		/// <inheritdoc />
		protected override void OnStart()
		{
			base.OnStart();

			Game.State = GameState.Finished;
			
			PlayerLocation spawn = null;

			Game.TryGetSpawnPosition(out spawn);

			foreach (var player in Game.Players)
			{
				player.HealthManager.ResetHealth();
				player.SetGamemode(GameMode.Spectator);

				if (spawn != null)
					player.Teleport(spawn);
			}
		}
	}
}