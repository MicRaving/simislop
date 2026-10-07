using FSO.Client;
using FSO.Client.GameContent;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Model;
using FSO.Common.Rendering.Framework.Model;
using FSO.SimAntics;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Simitone.Client.UI.Panels.Cheats;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels
{
    public class UICheatTextbox : UIContainer
    {
        /// <summary>
        /// The cheats the VM executes, rather than the client. Kept as a member rather than inlined
        /// so the keyword table lives in one place: <see cref="CheatCommands"/> reads the same
        /// dictionary to print the list of cheats, and a keyword that is playable but missing from
        /// that list is a cheat nobody can find.
        /// </summary>
        private readonly IReadOnlyDictionary<string, VMCheatContext.VMCheatType> cheatDefinitions =
            CheatCommands.NetCheats;

        private UITextBox baseTextbox;
        private Texture2D baseTexture;
        private VM ts1VM;
        private CheatRunner runner;

        /// <summary>
        /// An empty UICheatTextbox
        /// </summary>
        public UICheatTextbox(FSO.SimAntics.VM vm) : this(vm, "")
        {
            
        }
        /// <summary>
        /// A UICheatTextbox with text
        /// </summary>
        /// <param name="initialText"></param>
        public UICheatTextbox(FSO.SimAntics.VM vm, string initialText)
        {
            ts1VM = vm;
            baseTextbox = new UITextBox()
            {
                CurrentText = initialText,
                FlashOnEmpty = true,
                MaxLines = 1,
                //Tooltip = "Cheaters never win",
                //FrameColor = Color.Transparent,
                ID = "UICheatTextboxBase",
                
            };
            baseTexture = new Texture2D(GameFacade.GraphicsDevice, 1, 1);
            baseTexture.SetData(new Color[] { new Color((byte)67, (byte)93, (byte)90, (byte)255) });
            baseTextbox.SetBackgroundTexture(baseTexture, 0, 0, 0, 0);
            Size = new Vector2(200, 23);
            baseTextbox.SetSize(200, 23);
            Add(baseTextbox);

            runner = new CheatRunner(vm, ResolveActiveSim);
        }

        /// <summary>
        /// The Sim a cheat that names one person applies to. Read from the game screen rather than
        /// kept here, because the selection is the lot control's and it changes while this box is
        /// up: a cheat typed after the player clicks a different Sim has to go to that Sim.
        /// </summary>
        private VMAvatar ResolveActiveSim()
        {
            return (UIScreen.Current as TS1GameScreen)?.LotControl?.ActiveEntity as VMAvatar;
        }
        public override void Update(UpdateState state)
        {
            base.Update(state);
            TickMotiveFreeze();
            var pressedKeys = state.KeyboardState.GetPressedKeys();         
            //not sure if ts1 allowed you to use right ctrl or right shift but i dont discriminate
            if ((pressedKeys.Contains(Keys.LeftControl) || pressedKeys.Contains(Keys.RightControl))
                && (pressedKeys.Contains(Keys.LeftShift) || pressedKeys.Contains(Keys.RightShift))
                && state.NewKeys.Contains(Keys.C)) //prevent change over multiple frames
            {
                Visible = !Visible;
                //The textbox takes input focus while it is up, and gives it up when it closes.
                //Leaving focus on a hidden element means keystrokes go to a box the player cannot
                //see, and the single-key shortcuts that ask "is any text input focused?" (the
                //screen's speed keys, the desktop UCP's) would keep reporting that something has
                //focus for the rest of the session.
                if (Visible) state.InputManager.SetFocus(baseTextbox);
                else state.InputManager.SetFocus(null);
            }
            baseTextbox.Visible = Visible;
            if (Visible)
            {
                if (state.NewKeys.Contains(Keys.Enter))
                {
                    commandEntered(baseTextbox.CurrentText, out bool shouldHide);
                    Visible = !shouldHide;
                    if (!Visible) state.InputManager.SetFocus(null);
                }
            }               
        }

        /// <summary>
        /// Enforces the freeze-needs cheat, once a game-minute.
        ///
        /// <remarks>
        /// Driven from here because this is the one cheat-system file that is on the screen's
        /// update path in every lot mode, and because it is a rule about the lot rather than
        /// about a control. Keyed off the clock's own minute counter rather than a timer, so it
        /// runs at the game's pace - a paused game freezes the cheat too, which is right, since
        /// nothing decays while the clock is stopped.
        /// </remarks>
        private void TickMotiveFreeze()
        {
            var minute = ts1VM?.Context?.Clock?.Minutes;
            if (minute == null) return;
            if (minute == lastFreezeMinute) return;
            lastFreezeMinute = minute.Value;
            MotiveDecayFreeze.Update(ts1VM);
        }

        /// <summary>
        /// The last game-minute the freeze was enforced on, so a per-frame update does the work
        /// once a minute rather than sixty times a second.
        /// </summary>
        private int? lastFreezeMinute;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="commandString"></param>
        private void commandEntered(string commandString, out bool shouldHide)
        {
            shouldHide = true;
            if (string.IsNullOrWhiteSpace(commandString)) return; // a blank textbox should close after hitting enter -- even if a command was never run.

            //Weather does not go through VMCheatContext: it is not a cheat, it is a lot setting
            //that the renderer reads, and it is answered by the lot's own weather controller.
            if (WeatherCommandParser.IsWeatherCommand(commandString))
            {
                HandleWeatherCommand(commandString);
                baseTextbox.CurrentText = "";
                return;
            }

            // The cheats the client carries out itself, tried before the VM's own cheat path.
            // This is a parse and a lookup, and it has to come first: `set_hour 9` is not a
            // VMCheatContext, and letting it reach the keyword table below would answer it with
            // the "not a command" error for a cheat that exists.
            if (TryRunClientCheat(commandString, out shouldHide)) return;

            var cheat = new VMNetCheatCmd();
            var context = new VMCheatContext();
            var repetitions = CheatCommands.Repetitions(commandString);
            // Case-insensitive, and on the trimmed line: `move_objects on` and `MoveObjects ON`
            // are the same cheat, and the keyword table is the one place that decides which
            // spellings exist.
            switch (trimRepetitions(commandString).ToLowerInvariant())
            {
                //These three are special cheatcodes that don't really match the parameterized cheats e.g. moveobjects on
                case "klapaucius":
                case "rosebud":
                    context.Amount = (int)VMCheatContext.BudgetCheatPresetAmount.KLAPAUCIUS;
                    context.CheatBehavior = VMCheatContext.VMCheatType.Budget;
                    break;
                case "motherlode":
                    context.Amount = (int)VMCheatContext.BudgetCheatPresetAmount.MOTHERLODE;
                    context.CheatBehavior = VMCheatContext.VMCheatType.Budget;
                    break;
                default: context = parseCommandString(commandString); break;                                        
            }
            cheat.Context = context;
            shouldHide = false;
            if (cheat.Context == null) // the command was not recognized
            {
                FSO.HIT.HITVM.Get().PlaySoundEvent(UISounds.Error); // in TS1 this was a dialog but a sound may be less intrusive
                return;
            }
            context.Repetitions = (byte)repetitions;
            var sndEvent = UISounds.Error;
            if (context.CheatBehavior != VMCheatContext.VMCheatType.InvalidCheat)
            {
                ts1VM.SendCommand(cheat);
                switch (context.CheatBehavior) // sound feedback
                {
                    case VMCheatContext.VMCheatType.Budget: sndEvent = UISounds.BuyPlace; break;
                    default: sndEvent = UISounds.Click; break;
                }
                shouldHide = true;
            }
            FSO.HIT.HITVM.Get().PlaySoundEvent(sndEvent);
        }

        /// <summary>
        /// Runs a cheat the client has to carry out itself, if the line is one.
        /// </summary>
        /// <remarks>
        /// Reports whether the line was one of these cheats, through
        /// <paramref name="shouldHide"/>. A line that is not (which is most lines) leaves it
        /// alone so the caller carries on to the VM's own cheat path, and a line that is but
        /// could not be carried out - a lot that has not finished loading - answers with the
        /// error sound and the box closes, exactly as an unrecognised line does. To a player
        /// those two are the same thing: the command did not do anything.
        /// </remarks>
        private bool TryRunClientCheat(string commandString, out bool shouldHide)
        {
            shouldHide = true;
            var command = CheatCommands.Parse(commandString);
            if (!command.IsKnown) return false;

            if (runner.Run(command, out string message) && message != null)
            {
                // The one cheat whose whole effect is words: `cheats` prints the list rather
                // than changing the lot, so it has to be shown somewhere.
                if (command.Action == CheatAction.ListCheats) ShowCheatList(message);
                FSO.HIT.HITVM.Get().PlaySoundEvent(UISounds.Click);
            }
            else
            {
                FSO.HIT.HITVM.Get().PlaySoundEvent(UISounds.Error);
            }
            return true;
        }

        private void ShowCheatList(string text)
        {
            // The alert is captured before the button that dismisses it is created, which is the
            // same shape the Options restart warning uses: the handler needs the dialog, and the
            // dialog is what the handler is handed.
            UIAlert alert = null;
            alert = UIScreen.GlobalShowAlert(new UIAlertOptions()
            {
                Message = text,
                Buttons = UIAlertButton.Ok(x => UIScreen.RemoveDialog(alert))
            }, true);
        }

        private String trimRepetitions(string input)
        {
            return input.Replace(";", "").Replace("!", "");
        }

        /// <summary>
        /// Creates a VMCheatContext which includes repetitions, parameters, and CheatBehavior
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        private VMCheatContext parseCommandString(string command)
        {
            if (command.Length == 0)
                return null;
            var baseCmd = command;

            int NumberOfParameters = 1; // in the future if we want multiple parameters per cheat...
            
            string[] parameters = new string[NumberOfParameters];
            if (command.Contains(' ')) {
                baseCmd = command.Substring(0, command.IndexOf(' '));
                string parameterString = command.Substring(command.IndexOf(' ') + 1);
                for (int i = 0; i < NumberOfParameters; i++)
                {
                    var individualParameter = parameterString;
                    if (string.IsNullOrWhiteSpace(individualParameter))
                        break;
                    if (individualParameter.Contains(' '))
                    {
                        individualParameter = individualParameter.Substring(0, individualParameter.IndexOf(' '));
                        parameters[i] = individualParameter;
                        parameterString = parameterString.Substring(parameterString.IndexOf(' ') + 1);
                        continue;
                    }                    
                    parameters[i] = individualParameter;
                    break;
                }
            }
            // The repetition marks have to come off the keyword itself, not just off the line:
            // `moveobjects!` is the same cheat as `moveobjects`, and the table is keyed on the
            // bare word. The original code called the trim and dropped the result, so a marked
            // keyword was looked up with its marks still on it.
            baseCmd = trimRepetitions(baseCmd).ToLowerInvariant();
            if (!cheatDefinitions.TryGetValue(baseCmd, out VMCheatContext.VMCheatType cheatType))
            {
                // cheat not defined in cheatDefinitions
                return null;
            }
            VMCheatContext context = new VMCheatContext()
            {
                CheatBehavior = cheatType,                
            };
            foreach(var parameter in parameters)
            {
                switch (parameter)
                {
                    case "on": context.Modifier = true; break; // set modifier true
                    case "off": context.Modifier = false; break; // set modifer false
                    default:
                        if (int.TryParse(parameter, out int amount)) //check if the parameter is a number
                            context.Amount = amount; // if it is amount is set
                        break;
                }
            }
            return context;
        }

        /// <summary>
        /// Applies a <c>weather &lt;type&gt; [intensity]</c> line to the lot's weather.
        /// </summary>
        /// <remarks>
        /// The parse itself is <see cref="WeatherCommandParser"/>'s, so the command table is
        /// testable without a VM; all that is left here is handing the result to the lot and
        /// answering with a sound. A line the parser rejects and a lot with no blueprint to change
        /// are the same thing to a player - the command did not do anything - so they answer
        /// alike.
        /// </remarks>
        /// <param name="command">The typed line, repetition marks and all.</param>
        private void HandleWeatherCommand(string command)
        {
            if (!WeatherCommandParser.TryParse(command, out short weatherData))
            {
                FSO.HIT.HITVM.Get().PlaySoundEvent(UISounds.Error);
                return;
            }

            var weather = ts1VM?.Context?.Blueprint?.Weather;
            if (weather == null)
            {
                //no lot, or its blueprint has not finished loading yet
                FSO.HIT.HITVM.Get().PlaySoundEvent(UISounds.Error);
                return;
            }

            weather.SetWeather(weatherData);
            SimitoneLog.Info("Weather cheat set the weather to " + WeatherCommandParser.Describe(weatherData) + ".");
            FSO.HIT.HITVM.Get().PlaySoundEvent(UISounds.Click);
        }
    }
}
