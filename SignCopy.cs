// Reference: System.Drawing

using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Configuration;
using Rust;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SignCopy", "Justin A", "1.0.0")]
    [Description("Allows users to save images from sign entities they have access to.")]
    class SignCopy : RustPlugin
    {
        // assistive values for getting current time
        private static readonly DateTime EPOCH = new DateTime(1970, 1, 1, 0, 0, 0);
        private const int SECONDS_IN_DAY = 86400;

        // layer mask detected for raycast collisions
        private static readonly int RAY_HIT_LAYERMASK = LayerMask.GetMask("Construction", "Deployed");

        // sub directory where folders/files are saved
        private const string SUB_DIRECTORY = "SignCopy/";
        private const string FILE_PREFIX = "image_";
        private const string SUBMIT_FOLDER_NAME = "0_submitted";

        // oxide permission constants
        private const string PERMISSION_USE = "signcopy.use";
        private const string PERMISSION_SUBMIT = "signcopy.submit";
        private const string PERMISSION_VIP = "signcopy.vip";
        private const string PERMISSION_ADMIN = "signcopy.admin";
        
        // dictionary of image sizes and english name for each corresponding sign.
        private static readonly Dictionary<string, SignDetail> SIGN_DETAILS = new Dictionary<string, SignDetail>()
        {
            {"sign.hanging", new SignDetail(128, 256, "Two Sided Hanging Sign")},
            {"sign.hanging.banner.large", new SignDetail(64, 256, "Large Banner Hanging")},
            {"sign.hanging.ornate", new SignDetail(256, 128, "Two Sided Ornate Hanging Sign")},
            {"sign.huge.wood", new SignDetail(512, 128, "Huge Wooden Sign")},
            {"sign.large.wood", new SignDetail(256, 128, "Large Wooden Sign")},
            {"sign.medium.wood", new SignDetail(256, 128, "Wooden Sign")},
            {"sign.pictureframe.landscape", new SignDetail(256, 128, "Landscape Picture Frame")},
            {"sign.pictureframe.portrait", new SignDetail(128, 256, "Portrait Picture Frame")},
            {"sign.pictureframe.tall", new SignDetail(128, 512, "Tall Picture Frame")},
            {"sign.pictureframe.xl", new SignDetail(512, 512, "XL Picture Frame")},
            {"sign.pictureframe.xxl", new SignDetail(1024, 512, "XXL Picture Frame")},
            {"sign.pole.banner.large", new SignDetail(64, 256, "Large Banner on pole")},
            {"sign.post.double", new SignDetail(256, 256, "Double Sign Post")},
            {"sign.post.single", new SignDetail(128, 64, "Single Sign Post")},
            {"sign.post.town", new SignDetail(256, 128, "One Sided Town Sign Post")},
            {"sign.post.town.roof", new SignDetail(256, 128, "Two Sided Town Sign Post")},
            {"sign.small.wood", new SignDetail(128, 64, "Small Wooden Sign")},
            //{"spinner.wheel.deployed", new SignDetail(512, 512, "Spinning wheel")},
        };
        
        // record keeping of user's cooldowns, resets when unloaded.
        private Dictionary<ulong, int> UsersLastSave;
        private Dictionary<ulong, int> UsersLastPaste;
        private Dictionary<ulong, int> UsersLastSubmit;


        #region PluginConfiguration

        // plugin json config file
        private Configuration config;

        public class Configuration
        {
            [JsonProperty(PropertyName = "Save Options")]
            public SaveOptions Save { get; set; }

            [JsonProperty(PropertyName = "Paste Options")]
            public PasteOptions Paste { get; set; }

            [JsonProperty(PropertyName = "Submit Options")]
            public SubmitOptions Submit { get; set; }

            [JsonProperty(PropertyName = "Misc Options")]
            public MiscOptions Misc { get; set; }

            public class SaveOptions
            {
                [JsonProperty(PropertyName = "Image save limit")]
                public int SaveLimit { get; set; } = 3;

                [JsonProperty(PropertyName = "Image save limit - VIP")]
                public int SaveLimitVIP { get; set; } = 5;

                [JsonProperty(PropertyName = "Save cooldown (seconds, 0 to disable)")]
                public int SaveCooldown { get; set; } = 120;
            }

            public class PasteOptions
            {
                [JsonProperty(PropertyName = "Paste cooldown (seconds, 0 to disable)")]
                public int PasteCooldown { get; set; } = 60;
            }

            public class SubmitOptions
            {
                [JsonProperty(PropertyName = "Enable submit (true/false)")]
                public bool EnableSubmit { get; set; } = false;

                [JsonProperty(PropertyName = "Notify admins when pending submissions")]
                public bool EnableSubmitNotify { get; set; } = false;

                [JsonProperty(PropertyName = "Image submission limit")]
                public int SubmitLimit { get; set; } = 2;

                [JsonProperty(PropertyName = "Image submission limit - VIP")]
                public int SubmitLimitVIP { get; set; } = 4;
                
                [JsonProperty(PropertyName = "Submit cooldown (seconds, 0 to disable)")]
                public int SubmitCooldown { get; set; } = 120;
            }

            public class MiscOptions
            {
                [JsonProperty(PropertyName = "Auto delete inactive user's data (days, 0 to disable)")]
                public int AutoDeleteDays { get; set; } = 90;

                [JsonProperty(PropertyName = "Auto delete inactive user's data - VIP (days, 0 to disable)")]
                public int AutoDeleteDaysVIP { get; set; } = 0;
            }

            public static Configuration DefaultConfig()
            {
                return new Configuration
                {
                    Save = new Configuration.SaveOptions(),
                    Paste = new Configuration.PasteOptions(),
                    Submit = new Configuration.SubmitOptions(),
                    Misc = new Configuration.MiscOptions()
                };
            }
        }
        
        protected override void LoadDefaultConfig() => config = Configuration.DefaultConfig();

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion
        
        
        #region ImageData

        // file access for image data
        private DynamicConfigFile SavedImagesFile;
		
		// Database of user saved images and submitted images
		private ImageData SavedImagesData;
		
		public class ImageData
        {
            [JsonProperty("Pending Submissions")]
            public Dictionary<ulong, List<SavedImageObject>> PendingSubmissions { get; set; } = new Dictionary<ulong, List<SavedImageObject>>();
			
			[JsonProperty("Player Signs")]
			public Dictionary<ulong, UserSaveData> UserImages { get; set; } = new Dictionary<ulong, UserSaveData>();
        }

        public class UserSaveData
        {
            [JsonProperty("Last Seen")]
            public int TimeLastSeen { get; set; } = 0;

            [JsonProperty("Saved Signs")]
            public List<SavedImageObject> SavedImages { get; set; } = new List<SavedImageObject>();
        }

        public class SavedImageObject
        {
            [JsonProperty("Slot ID")]
            public int IndexID { get; set; } = 0;

            [JsonProperty("Image Name")]
            public string ImageName { get; set; } = "emptyname";

            [JsonProperty("Original Sign Entity")]
            public string OriginalSign { get; set; } = "some sign";
        }
        
		private void SaveUserImageData()
        {
            if (SavedImagesData == null) return;
            SavedImagesFile.WriteObject(SavedImagesData);
        }
		
		private DynamicConfigFile GetFile(string name)
        {
            var file = Interface.Oxide.DataFileSystem.GetFile(name);
            file.Settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            file.Settings.Converters = new JsonConverter[] {
                new CustomComparerDictionaryCreationConverter<string>(StringComparer.OrdinalIgnoreCase)};
            return file;
        }
        
        #endregion


        #region Localization

        private new void LoadDefaultMessages()
        {
            // English
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Error_DuplicateName"] = "Saved image already exists with this name.",
                ["Error_ImageNotFound"] = "No match found with {0}",
                ["Error_LimitExceeded"] = "You are already at your {0} limit.",
                ["Error_NoFileFound"] = "File .../{0}/{1} not found.",
                ["Error_NoOxidePermission"] = "You do not have permission to use this command.",
                ["Error_NoPlayerData"] = "Player directory not found.",
                ["Error_NoSign"] = "Didn't find a sign.",
                ["Error_NoSignPermission"] = "You do not have permission for this sign.",
                ["Error_NotInteger"] = "Invalid integer",
                ["Error_OnCooldown"] = "<color=yellow>{0}</color> can't be used for another {1}.",
                ["Error_ReadingImage"] = "Error reading image.",
                ["Error_SubmitDisabled"] = "Submit feature is disabled.",
                ["Help_General"] = "/sign submit <name>\n/sign save <name>\n/sign paste <name>\n/sign list.",
                ["Help_List_Header"] = "Saved Signs\n------------------",
                ["Help_List_Image"] = "{0}.  {1} <color=grey>- {2}</color>",
                ["Help_Paste"] = "/sign paste <name/number>.",
                ["Help_Remove"] = "/sign remove <name>.",
                ["Help_Save"] = "/sign save <name>.",
                ["Help_Submit"] = "/sign submit <name>.",
                ["Info_PurgeEnd"] = "... Purged {0} user's data.",
                ["Info_PurgeStart"] = "Purging {0}({1} vip)+ inactive days of user's data...",
                ["Info_Submissions"] = "[<color=lime>SignCopy</color>] <color=yellow>{0}</color> pending submissions.",
                ["Misc_Hours"] = "<color=#ff8000>{0}</color><size=12>h</size> <color=#ff8000>{1}</color><size=12>min</size>",
                ["Misc_Minutes"] = "<color=#ff8000>{0}</color><size=12>min</size> <color=#ff8000>{1}</color><size=12>s</size>",
                ["Misc_Seconds"] = "<color=#ff8000>{0}</color><size=12>s</size>",
                ["Success_Paste"] = "Sign image \"<color=yellow>{0}</color>\" pasted.",
                ["Success_Remove"] = "Sign image \"<color=yellow>{0}</color>\" removed.",
                ["Success_Save"] = "Sign image \"<color=yellow>{0}</color>\" saved.",
                ["Success_Submit"] = "Sign image \"<color=yellow>{0}</color>\" submitted for admin review.",
                //["aaaaa"] = "aaaaaaaa",
            }, this);
            
            // Spanish
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Error_DuplicateName"] = "La imagen guardada ya existe con este nombre.",
                ["Error_ImageNotFound"] = "No se encontró ninguna coincidencia con {0}",
                ["Error_LimitExceeded"] = "Ya estás en tu límite de {0}",
                ["Error_NoFileFound"] = "Archivo .../{0}/{1} no encontrado.",
                ["Error_NoOxidePermission"] = "No tiene permiso para usar este comando.",
                ["Error_NoPlayerData"] = "Directorio del reproductor no encontrado.",
                ["Error_NoSign"] = "No encontré un signo.",
                ["Error_NoSignPermission"] = "No tiene permiso para este signo.",
                ["Error_NotInteger"] = "Número entero no válido",
                ["Error_OnCooldown"] = "<color=yellow>{0}</color> no se puede usar para otro {1}.",
                ["Error_ReadingImage"] = "Error al leer la imagen.",
                ["Error_SubmitDisabled"] = "Enviar característica está deshabilitada.",
                ["Help_General"] = "/sign submit <nombre> \n/sign save <nombre> \n/sign paste <nombre> \n/sign list.",
                ["Help_List_Header"] = "Signos guardados \n ------------------",
                ["Help_List_Image"] = "{0}. {1}<color=gray> - {2}</color>",
                ["Help_Paste"] = "/sign paste <nombre/número>.",
                ["Help_Remove"] = "/sign remove <nombre>.",
                ["Help_Save"] = "/sign save <nombre>.",
                ["Help_Submit"] = "/sign submit <nombre>.",
                ["Info_PurgeEnd"] = "... Depuró los datos del jugador {0}.",
                ["Info_PurgeStart"] = "Depuración {0} ({1} vip) + días inactivos de los datos del jugador ...",
                ["Info_Submissions"] = "[<color=lime>SignCopy</color>] <color=yellow>{0}</color> envíos pendientes.",
                ["Misc_Hours"] = "<color=#ff8000>{0}</color><size=12>h</size> <color=#ff8000>{1}</color><size=12>min</size> ",
                ["Misc_Minutes"] = "<color=#ff8000>{0}</color><size=12>min</size> <color=#ff8000>{1}</color><size=12>s</size> ",
                ["Misc_Seconds"] = "<color=#ff8000>{0}</color><size=12>s</size>",
                ["Success_Paste"] = "Imagen de signo \"<color=yellow>{0}</color>\" pegado.",
                ["Success_Remove"] = "Imagen de muestra \"<color=yellow>{0}</color>\" eliminada.",
                ["Success_Save"] = "Imagen de signo \"<color=yellow>{0}</color>\" guardado.",
                ["Success_Submit"] = "Imagen de muestra \"<color=yellow>{0}</color>\" enviada para la revisión del administrador.",
            }, this, "es");

            // German
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Error_DuplicateName"] = "Gespeichertes Bild existiert bereits mit diesem Namen.",
                ["Error_ImageNotFound"] = "Keine Übereinstimmung mit {0} gefunden",
                ["Error_LimitExceeded"] = "Sie sind bereits an Ihrem {0} Limit.",
                ["Error_NoFileFound"] = "Datei .../{0}/{1} nicht gefunden.",
                ["Error_NoOxidPermission"] = "Sie sind nicht berechtigt, diesen Befehl zu verwenden.",
                ["Error_NoPlayerData"] = "Spielerverzeichnis nicht gefunden.",
                ["Error_NoSign"] = "Ich habe kein Zeichen gefunden.",
                ["Error_NoSignPermission"] = "Sie haben keine Berechtigung für dieses Zeichen.",
                ["Error_NotInteger"] = "Ungültige Ganzzahl",
                ["Error_OnCooldown"] = "<color=yellow>{0}</color> kann nicht für ein anderes {1} verwendet werden.",
                ["Error_ReadingImage"] = "Fehler beim Lesen des Bildes.",
                ["Error_SubmitDisabled"] = "Sendefunktion ist deaktiviert.",
                ["Help_General"] = "/sign submit <Name> \n/sign save <Name> \n/sign <Name> \n/sign list.",
                ["Help_List_Header"] = "Gespeicherte Zeichen \n ------------------",
                ["Help_List_Image"] = "{0}. {1}<color=grey> - {2}</color>",
                ["Help_Paste"] = "/sign paste <Name/Nummer>.",
                ["Help_Remove"] = "/sign remove <Name>.",
                ["Help_Save"] = "/sign save <name>.",
                ["Help_Submit"] = "/sign submit <Name>.",
                ["Info_PurgeEnd"] = "... Spielerdaten von {0} gelöscht.",
                ["Info_PurgeStart"] = "{0} ({1} vip) löschen + Inaktive Tage der Spielerdaten ...",
                ["Info_Submissions"] = "[<color=lime>SignCopy</color>] <color=yellow>{0}</color> ausstehende Übermittlungen.",
                ["Misc_Hours"] = "<color=#ff8000>{0}</color><size=12>h</size> <color=#ff8000>{1}</color><size=12>min</size> ",
                ["Misc_Minutes"] = "<color=#ff8000>{0}</color><size=12>min</size> <color=#ff8000>{1}</color><size=12>s</size> ",
                ["Misc_Seconds"] = "<color=#ff8000>{0}</color><size=12>s</size>",
                ["Success_Paste"] = "Bild signieren \"<color=yellow>{0}</color>\" eingefügt.",
                ["Success_Remove"] = "Bild signieren \"<color=yellow>{0}</color>\" entfernt.",
                ["Success_Save"] = "Bild signieren \"<color=yellow>{0}</color>\" gespeichert.",
                ["Success_Submit"] = "Bild signieren \"<color=yellow>{0}</color>\" wurde zur Überprüfung durch den Administrator gesendet.",
            }, this, "de");

            // French
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Error_DuplicateName"] = "L'image enregistrée existe déjà avec ce nom.",
                ["Error_ImageNotFound"] = "Aucune correspondance trouvée avec {0}",
                ["Error_LimitExceeded"] = "Vous avez déjà atteint la limite de {0}.",
                ["Error_NoFileFound"] = "Fichier .../{0}/{1} non trouvé.",
                ["Error_NoOxidePermission"] = "Vous n'avez pas la permission d'utiliser cette commande.",
                ["Error_NoPlayerData"] = "Répertoire du lecteur introuvable.",
                ["Error_NoSign"] = "N'a pas trouvé de signe.",
                ["Error_NoSignPermission"] = "Vous n'avez pas la permission pour ce signe.",
                ["Error_NotInteger"] = "Entier non valide",
                ["Error_OnCooldown"] = "<color=yellow>{0}</color> ne peut pas être utilisé pour un autre {1}.",
                ["Error_ReadingImage"] = "Erreur lors de la lecture de l'image.",
                ["Error_SubmitDisabled"] = "La fonctionnalité de soumission est désactivée.",
                ["Help_General"] = "/sign submit <nom> \n/sign save <nom> \n/sign remove <nom> \n/sign list.",
                ["Help_List_Header"] = "Signes enregistrés \n ------------------",
                ["Help_List_Image"] = "{0}. {1}<color=grey> - {2}</color>",
                ["Help_Paste"] = "/signer paste <nom/numéro>.",
                ["Help_Remove"] = "/sign remove <nom>.",
                ["Help_Save"] = "/signe save <nom>.",
                ["Help_Submit"] = "/sign submit <nom>.",
                ["Info_PurgeEnd"] = "... Purged {0} les données du joueur.",
                ["Info_PurgeStart"] = "Purge {0} ({1} vip) + jours inactifs des données du joueur ...",
                ["Info_Submissions"] = "[<color=lime>SignCopy</color>] <color=yellow>{0}</color> en attente de soumission.",
                ["Misc_Hours"] = "<color=#ff8000>{0}</color><size=12>h</size> <color=#ff8000>{1}</color><size=12>min</size> ",
                ["Misc_Minutes"] = "<color=#ff8000>{0}</color><size=12>min</size> <color=#ff8000>{1}</color><size=12>s</size> ",
                ["Misc_Seconds"] = "<color=#ff8000>{0}</color><size=12>s</size>",
                ["Success_Paste"] = "Signe image \"<color=yellow>{0}</color>\" collé.",
                ["Success_Remove"] = "Signe image \"<color=yellow>{0}</color>\" supprimé.",
                ["Success_Save"] = "Signe image \"<color=yellow>{0}</color>\" enregistré.",
                ["Success_Submit"] = "Signe image \"<color=yellow>{0}</color>\" soumis pour révision par l'administrateur.",
            }, this, "fr");

            // Russian
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Error_DuplicateName"] = "Сохраненное изображение уже существует с этим именем.",
                ["Error_ImageNotFound"] = "Нет совпадений с {0}",
                ["Error_LimitExceeded"] = "Вы уже на пределе {0}.",
                ["Error_NoFileFound"] = "Файл .../{0}/{1} не найден.",
                ["Error_NoOxidePermission"] = "У вас нет разрешения на использование этой команды.",
                ["Error_NoPlayerData"] = "Каталог проигрывателя не найден.",
                ["Error_NoSign"] = "Не находил знака.",
                ["Error_NoSignPermission"] = "У вас нет разрешения на этот знак.",
                ["Error_NotInteger"] = "Недопустимое целое число",
                ["Error_OnCooldown"] = "<color=yellow>{0}</color> нельзя использовать для другого {1}.",
                ["Error_ReadingImage"] = "Ошибка чтения изображения.",
                ["Error_SubmitDisabled"] = "Функция отправки отключена.",
                ["Help_General"] = "/sign submit <имя> \n/sign save <имя> \n/sign remove <имя> \n/sign list.",
                ["Help_List_Header"] = "Сохраненные значки \n ------------------",
                ["Help_List_Image"] = "{0}. {1}<color=grey> - {2}</color>",
                ["Help_Paste"] = "/sign paste <имя/номер>.",
                ["Help_Remove"] = "/sign remove <имя>.",
                ["Help_Save"] = "/sign save <имя>.",
                ["Help_Submit"] = "/sign submit <имя>.",
                ["Info_PurgeEnd"] = "... Purged {0} данные игрока.",
                ["Info_PurgeStart"] = "Очистка {0} ({1} vip) + неактивные дни данных игрока ...",
                ["Info_Submissions"] = "[<color=lime>SignCopy</color>] <color=yellow>{0}</color> ожидающие представления.",
                ["Misc_Hours"] = "<color=#ff8000>{0}</color><size=12>h</size> <color=#ff8000>{1}</color><size=12>min</size>",
                ["Misc_Minutes"] = "<color=#ff8000>{0}</color><size=12>min</size> <color=#ff8000>{1}</color><size=12>s</size>",
                ["Misc_Seconds"] = "<color=#ff8000>{0}</color><size=12>s</size>",
                ["Success_Paste"] = "Подписать изображение \"<color=yellow>{0}</color>\" вставить.",
                ["Success_Remove"] = "Изображение знака \"<color=yellow>{0}</color>\" удаленный.",
                ["Success_Save"] = "Изображение знака \"<color=yellow>{0}</color>\" сохранены.",
                ["Success_Submit"] = "Изображение знака \"<color=yellow>{0}</color>\" отправлено для просмотра администратор.",
            }, this, "ru");

        }

        #endregion


        #region OxideHooks

        /*
         * writes user saved images to database
         */
        void OnServerSave()
        {
            // delay data saving to help prevent latency issues
            timer.Once(15f, () => { SaveUserImageData(); });
        }
        
        /*
         * oxide runs Init() when plugin is being initialized.
         * Initializes needed files
         */
        void Init()
        {
            //instance = this;

            UsersLastSave = new Dictionary<ulong, int>();
            UsersLastPaste = new Dictionary<ulong, int>();
            UsersLastSubmit = new Dictionary<ulong, int>();

            // load image data
            SavedImagesFile = GetFile(nameof(SignCopy) + "");
            SavedImagesData = SavedImagesFile.ReadObject<ImageData>();

            // register user oxide permissions
            permission.RegisterPermission(PERMISSION_USE, this);
            permission.RegisterPermission(PERMISSION_VIP, this);
            permission.RegisterPermission(PERMISSION_SUBMIT, this);
            permission.RegisterPermission(PERMISSION_ADMIN, this);

            // load localized strings
            LoadDefaultMessages();
        }

        /*
         * Oxide loads the configuration during initialization.
         * Attempts to read saved config file. Generates a new
         * one if reading fails.
         */
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config?.Save == null) LoadDefaultConfig();
            }
            catch
            {
                PrintWarning($"Could not read oxide/config/{Name}.json, creating new config file");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        /*
         * oxide runs Loaded() after plugin is initialized.
         * Checks config to see if any oxide hooks are not needed
         * and should be disabled.
         */
        void Loaded()
        {
			if (!config.Submit.EnableSubmit || !config.Submit.EnableSubmitNotify)
                Unsubscribe(nameof(OnPlayerInit));

            if (config.Misc.AutoDeleteDays == 0)
            {
                Unsubscribe(nameof(OnNewSave));
                Unsubscribe(nameof(OnPlayerDisconnected));
            }
        }

        /*
         * Will purge user's data that have not been logged to the server
         * longer than the time specified in the configuration file.
         */
        void OnNewSave(string filename)
        {
            if (config.Misc.AutoDeleteDays != 0)
                PurgeInactive();
        }

        /*
         * Saves user image data when the server is running and the 
         * plugin is unloaded.
         */
        void Unload()
		{
			SaveUserImageData();
		}

        /*
         * Logs the time when the user as disconnected from the server.
         */
        void OnPlayerDisconnected(BasePlayer user, string reason)
        {
            if (user == null)
                return;

            UserSaveData pData;
            if (!SavedImagesData.UserImages.TryGetValue(user.userID, out pData))
                return;

            pData.TimeLastSeen = (int) GetCurrentTime();
        }

        /*
         * Informs users who have administrative permissions of pending submissions
         * when they connect to the server when they are available.
         */
        void OnPlayerInit(BasePlayer user)
        {
            if (user == null || !HasAccess(user, PERMISSION_ADMIN))
                return;

            if (SavedImagesData.PendingSubmissions.Count() > 0)
                MessageUserWhenAvailable(user);
        }

        /*
         * Repeatedly ping the user every few seconds to see if they are
         * available for message payload.
         */
        private void MessageUserWhenAvailable(BasePlayer user)
        {
            timer.Once(10f, () =>
            {
                if (user.IsConnected)
                {
                    if (user.IsDead() || user.IsSleeping())
                        MessageUserWhenAvailable(user);
                    else
                        MessageUserSubmissionCount(user);
                }
            });
        }

        /*
         * Send message payload to user indicating submission count.
         */
        private void MessageUserSubmissionCount(BasePlayer user)
        {
            int count = 0;
            foreach (KeyValuePair<ulong, List<SavedImageObject>> entry in SavedImagesData.PendingSubmissions)
            {
                count += entry.Value.Count();
            }
            SendMessage(user, "Info_Submissions", count);
        }

        #endregion
        

        /* Alternative commands */
        [ChatCommand("image")]
        void cmdSignImageA(BasePlayer user, string command, string[] args) { cmdSignImage(user, command, args); }
        [ChatCommand("img")]
        void cmdSignImageB(BasePlayer user, string command, string[] args) { cmdSignImage(user, command, args); }

        /*
         * Command for users with granted permissions to access the plugin
         * features.
         */
        [ChatCommand("sign")]
        void cmdSignImage(BasePlayer user, string command, string[] args)
        {
            // error checking
            if (!HasAccess(user, PERMISSION_USE) && !HasAccess(user, PERMISSION_ADMIN))
            {
                SendMessage(user, "Error_NoOxidePermission");
                return;
            }
			
            // parse subsequent commands in the args
			if (args.Length > 0)
			{
                switch (args[0])
				{
					case "submit":
                        ProcessSubmitCommand(user, args);
						break;
					case "save":
					case "copy":
					case "c":
                        ProcessSaveCommand(user, args);
						break;
					case "paste":
					case "p":
                        ProcessPasteCommand(user, args);
						break;
					case "remove":
                    case "delete":
                        ProcessRemoveCommand(user, args);
						break;
					case "list":
						ProcessListCommand(user);
						break;
					default:
						SendMessage(user, "Help_General");
						break;
				}
			}
			else
			{
				SendMessage(user, "Help_General");
			}
		}


        // Submit Image Command //
        /* 
         * Processes user submit command for validity.
         */
        private void ProcessSubmitCommand(BasePlayer user, string[] args)
        {
            // error checks
            int timeLeft;
            if (!config.Submit.EnableSubmit)
            {
                SendMessage(user, "Error_SubmitDisabled");
                return;
            }
            if (!HasAccess(user, PERMISSION_SUBMIT))
            {
                SendMessage(user, "Error_NoOxidePermission");
                return;
            }
            if (IsOnCooldown(UsersLastSubmit, user.userID, config.Submit.SubmitCooldown, out timeLeft))
            {
                SendMessage(user, "Error_OnCooldown", args[0], SecondsToReadable(timeLeft));
                return;
            }
            // end error checks
            
            if (args.Length > 1)
            {
                // parse rest of string args as imageName
                string imageName = String.Join(" ", args.Skip(1).ToArray());
                SubmitSignImage(user, imageName);
            }
            else
                SendMessage(user, "Help_Submit");
        }

        /*
         * Prepares data storage for the submitted image.
         */
        private void SubmitSignImage(BasePlayer user, string imageName)
		{
            // error checking and prepping
            Signage sign;
            if (!TryGetSign(user, out sign))
                return;

            List<SavedImageObject> userSubmittedImages;
            if (!SavedImagesData.PendingSubmissions.TryGetValue(user.userID, out userSubmittedImages))
                SavedImagesData.PendingSubmissions[user.userID] = userSubmittedImages = new List<SavedImageObject>();

            int submitLimit = HasAccess(user, PERMISSION_VIP) ? config.Submit.SubmitLimitVIP : config.Submit.SubmitLimit;
            if (userSubmittedImages.Count() >= submitLimit)
            {
                SendMessage(user, "Error_LimitExceeded", "submit");
                return;
            }
            if (userSubmittedImages.Exists(x => string.Equals(x.ImageName, imageName, StringComparison.OrdinalIgnoreCase)))
            {
                SendMessage(user, "Error_DuplicateName");
                return;
            }
            // end error checks
            
            string fileName = GetSubmitFileName(user.userID, imageName);
            SavedImageObject signObject;
            if (WriteImageToData(user, sign, SUBMIT_FOLDER_NAME, fileName, imageName, out signObject))
            {
                userSubmittedImages.Add(signObject);
                SendMessage(user, "Success_Submit", imageName);
                SetCooldownUsed(UsersLastSubmit, user.userID, config.Submit.SubmitCooldown);
            }
			else
				SendMessage(user, "Error_ReadingImage");
		}
        // End submit image command


        // Save Image Command //
        /* 
         * Processes user save command for validity.
         */
        private void ProcessSaveCommand(BasePlayer user, string[] args)
        {
            int timeLeft;
            if (IsOnCooldown(UsersLastSave, user.userID, config.Save.SaveCooldown, out timeLeft))
            {
                SendMessage(user, "Error_OnCooldown", args[0], SecondsToReadable(timeLeft));
                return;
            }

            if (args.Length > 1)
            {
                string fileName = String.Join(" ", args.Skip(1).ToArray());
                SaveSignImage(user, fileName);
            }
            else
                SendMessage(user, "Help_Save");
        }

        /*
         * Prepares data storage for the saved image.
         */
		private void SaveSignImage(BasePlayer user, string imageName)
		{
            // error checks and prepping
            Signage sign;
            if (!TryGetSign(user, out sign))
                return;
            
            UserSaveData pData;
            if (!SavedImagesData.UserImages.TryGetValue(user.userID, out pData))
                SavedImagesData.UserImages[user.userID] = pData = new UserSaveData();
            List<SavedImageObject> userSignList = pData.SavedImages;

            int saveLimit = HasAccess(user, PERMISSION_VIP) ? config.Save.SaveLimitVIP : config.Save.SaveLimit;
            if (userSignList.Count() >= saveLimit)
            {
                SendMessage(user, "Error_LimitExceeded", "save");
                return;
            }
            if (userSignList.Exists(x => string.Equals(x.ImageName, imageName, StringComparison.OrdinalIgnoreCase)))
            {
                SendMessage(user, "Error_DuplicateName");
                return;
            }
            // end error checks

            //userSignList.Sort((x, y) => (x.id, y.id));
            int fileIndex = GetAvailableFileIndex(userSignList.OrderBy(x => x.IndexID).ToList());
            string fileName = GetFileName(fileIndex);

            SavedImageObject signObject;
            if (WriteImageToData(user, sign, user.userID.ToString(), fileName, imageName, out signObject, fileIndex))
            {
                userSignList.Add(signObject);
                SendMessage(user, "Success_Save", imageName);
                SetCooldownUsed(UsersLastSave, user.userID, config.Save.SaveCooldown);
            }
			else
                SendMessage(user, "Error_ReadingImage");
		}
        // End save image command


        // Paste Image Command //
        /* 
         * Processes user paste command for validity.
         */
        private void ProcessPasteCommand(BasePlayer user, string[] args)
        {
            int timeLeft;
            if (IsOnCooldown(UsersLastPaste, user.userID, config.Paste.PasteCooldown, out timeLeft))
            {
                SendMessage(user, "Error_OnCooldown", args[0], SecondsToReadable(timeLeft));
                return;
            }

            if (args.Length > 1)
            {
                bool isNum = false;
                if (args.Length == 2)
                    if (IsInt(args[1])) isNum = true;

                string fileName = String.Join(" ", args.Skip(1).ToArray());
                SetImageOnSign(user, fileName, isNum);
            }
            else
                SendMessage(user, "Help_Paste");
        }

        /*
         * Sets the image texture on the sign to the specified image
         * from the user's image storage.
         */
        private void SetImageOnSign(BasePlayer user, string imageName, bool isNum = false)
		{
            Signage sign;
            if (!TryGetSign(user, out sign))
                return;

            UserSaveData pData;
            if (!SavedImagesData.UserImages.TryGetValue(user.userID, out pData))
			{
				SendMessage(user, "Error_NoPlayerData");
				return;
			}
			
			string fileName = GetFileName(user, imageName, pData.SavedImages, isNum);
            if (string.IsNullOrEmpty(fileName)) return;

            string path = GetFilePath(user.userID.ToString(), fileName);
			if (!Interface.Oxide.DataFileSystem.ExistsDatafile(path))
			{
                SendMessage(user, "Error_NoFileFound", user.userID, fileName);
				//userSignList.Remove(name);
                // haven't decided if I want to remove from reference table
				return;
			}
			var CopyData = Interface.Oxide.DataFileSystem.GetDatafile(path);

			byte[] imageBytes = Convert.FromBase64String((string)CopyData["SignData"]);
						
			ResizeImageToSign(sign, imageBytes);
			sign.SendNetworkUpdate();

            SendMessage(user, "Success_Paste", (string)CopyData["_ImageName"]);
            SetCooldownUsed(UsersLastPaste, user.userID, config.Paste.PasteCooldown);
        }
        // End paste image command


        // Remove Image Command //
        /* 
         * Processes user remove command for validity.
         */
        private void ProcessRemoveCommand(BasePlayer user, string[] args)
        {
            if (args.Length > 1)
            {
                bool isNum = false;
                if (args.Length == 2)
                    if (IsInt(args[1])) isNum = true;

                string imageName = String.Join(" ", args.Skip(1).ToArray());
                RemoveImageFromData(user, user.userID.ToString(), imageName, isNum);
            }
            else
                SendMessage(user, "Help_Remove");
        }

        /*
         * Removes the image from the user's stored images.
         */
        private void RemoveImageFromData(BasePlayer user, string folder, string imageName, bool isNum = false)
        {
            UserSaveData pData;
            if (!SavedImagesData.UserImages.TryGetValue(user.userID, out pData))
            {
                SendMessage(user, "Error_NoPlayerData");
                return;
            }
            List<SavedImageObject> userSignList = pData.SavedImages;
            
            string fileName = GetFileName(user, imageName, userSignList, isNum);
            if (string.IsNullOrEmpty(fileName)) return;

            ClearFileData(user.userID, fileName);

            // Gets the name of the file
            string trueName;
            if (isNum)
                trueName = FindUserImage(user, StringToInt(imageName), userSignList).ImageName;
            else
                trueName = FindUserImage(user, imageName, userSignList).ImageName;

            // Removes the image from reference data
            userSignList.RemoveAll(x => string.Equals(x.ImageName, trueName, StringComparison.OrdinalIgnoreCase));

            // If no images left in this user's data, remove user's profile.
            if (userSignList.Count() == 0)
            {
                SavedImagesData.UserImages.Remove(user.userID);
                MarkUserFolderForDelete(user.userID);
            }
            
            SendMessage(user, "Success_Remove", trueName);
        }
        // End remove image command


        // List Image Command //
        /*
         * Displays the user's saved images with their corresponding
         * reference index.
         */
        private void ProcessListCommand(BasePlayer user)
        {
            UserSaveData pData;
            if (!SavedImagesData.UserImages.TryGetValue(user.userID, out pData))
            {
                SendMessage(user, "Error_NoPlayerData");
                return;
            }
            List<SavedImageObject> userSignList = pData.SavedImages;

            StringBuilder sb = new StringBuilder();
            sb.Append(Lang("Help_List_Header"));

            userSignList.Sort((x, y) => string.Compare(x.ImageName, y.ImageName, StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < userSignList.Count(); i++)
            {
                sb.AppendLine();
                sb.Append(Lang("Help_List_Image", user.UserIDString, (i + 1), userSignList[i].ImageName, userSignList[i].OriginalSign));
            }

            user.ChatMessage(sb.ToString());
        }
        // End list image command


        /*
         * Finds the first unused file index from the provided images
         */
        private int GetAvailableFileIndex(List<SavedImageObject> imageList)
        {
            int fileIndex = 0;
            for (int i = 1; i <= imageList.Count(); i++)
            {
                if (imageList[i - 1].IndexID != i)
                {
                    // found unused id index
                    fileIndex = i;
                    break;
                }
            }
            if (fileIndex == 0)
            {
                fileIndex = imageList.Count() + 1;
            }
            return fileIndex;
        }

        
        /*
         * Assembles a file name from the given parameters. 
         * If isNum is true, will find user's image from
         * imageName as index.
         */
        private string GetFileName(BasePlayer user, string imageName, List<SavedImageObject> userImageList, bool isNum = false)
        {
            // TODO: Refactor to not use isNum
            SavedImageObject imageSave;
            if (isNum)
                imageSave = FindUserImage(user, StringToInt(imageName), userImageList);
            else
                imageSave = FindUserImage(user, imageName, userImageList);


            if (imageSave != null)
                return GetFileName(imageSave.IndexID);
            else
                return string.Empty;
        }

        /*
         * Gets the image's file name.
         */
        private string GetFileName(string imageName, List<SavedImageObject> userSignList)
        {
            if (userSignList.Exists(x => string.Equals(x.ImageName, imageName, StringComparison.OrdinalIgnoreCase)))
                return GetFileName(userSignList.Find(x => string.Equals(x.ImageName, imageName, StringComparison.OrdinalIgnoreCase)).IndexID);
            else
                return string.Empty;
        }

        /*
         * Returns the standard file name for users.
         */
        private string GetFileName(int saveID) { return FILE_PREFIX + saveID; }
        
        /* 
         * Returns the standard file path. 
         */
        private string GetFilePath(string folder, string fileName) { return SUB_DIRECTORY + folder + "/" + fileName; }

        /* 
         * Returns the standard file name for submitted files. 
         */
        private string GetSubmitFileName(ulong userID, string imageName) { return userID.ToString() + "_" + imageName; }

        /*
         * Find the user's saved image that matches the specified
         * image name.
         */
        private SavedImageObject FindUserImage(BasePlayer user, string imageName, List<SavedImageObject> userImageList)
        {
            if (!userImageList.Exists(x => string.Equals(x.ImageName, imageName, StringComparison.OrdinalIgnoreCase)))
            {
                SendMessage(user, "Error_ImageNotFound", imageName);
                ProcessListCommand(user);
                return null;
            }

            return userImageList.Find(x => string.Equals(x.ImageName, imageName, StringComparison.OrdinalIgnoreCase));
        }

        /*
         * Find the user's saved image that matches the specified
         * image index.
         */
        private SavedImageObject FindUserImage(BasePlayer user, int imageIndex, List<SavedImageObject> userImageList)
        {
            if (imageIndex < 1 || imageIndex > userImageList.Count())
            {
                SendMessage(user, "Error_NotInteger");
                ProcessListCommand(user);
                return null;
            }

            userImageList.Sort((x, y) => string.Compare(x.ImageName, y.ImageName, StringComparison.OrdinalIgnoreCase));
            return userImageList[imageIndex - 1];
        }

        /*
         * Writes sign image and info to user's data storage.
         */
        private bool WriteImageToData(BasePlayer user,
                                Signage sign, 
                                string folder, 
                                string fileName, 
                                string imageName, 
                                out SavedImageObject signObject, 
                                int fileIndex = 0)
		{
			bool result = false;
			var imageByte = FileStorage.server.Get(sign.textureID, FileStorage.Type.png, sign.net.ID);
			if(sign.textureID > 0 && imageByte != null)
			{
                // ItemManager searching causing problems
                //string signItemName = ItemManager.FindItemDefinition(sign.ShortPrefabName).displayName.english;
                string signItemName = SIGN_DETAILS.ContainsKey(sign.ShortPrefabName) ? SIGN_DETAILS[sign.ShortPrefabName].name : sign.ShortPrefabName;
                string path = GetFilePath(folder, fileName);

                var CopyData = Interface.Oxide.DataFileSystem.GetDatafile(path);

				CopyData.Clear();
                CopyData["_ImageName"] = imageName;
				CopyData["_Submitter"] = user.displayName;
				CopyData["_SubmitterID"] = user.userID;
				CopyData["_SignOwner"] = sign.OwnerID;
				CopyData["_OriginalSign"] = sign.ShortPrefabName;
				CopyData["_OriginalSignEnglish"] = signItemName;
				CopyData["SignData"] = Convert.ToBase64String(imageByte);

				Interface.Oxide.DataFileSystem.SaveDatafile(path);

                //userSignList.Add(new SavedSignObject { id = fileIndex, name = fileName, originalSign = signItemName });
                signObject = new SavedImageObject { IndexID = fileIndex, ImageName = imageName, OriginalSign = signItemName };

                result = true;
			}
            else
            {
                signObject = null;
            }
            return result;
		}

        
		/*
         * Returns the sign entity the user's raycast collided with.
         */
		private bool TryGetSign(BasePlayer user, out Signage sign)
		{
			RaycastHit hit;
			
            // check if raycast collided with any valid entities
			if(!Physics.Raycast(user.eyes.HeadRay(), out hit, 3f, RAY_HIT_LAYERMASK))
            {
                SendMessage(user, "Error_NoSign");
                sign = null;
                return false;
            }
			
            sign = hit.GetEntity().GetComponentInParent<Signage>();

            // check if entity hit is a signage entity
            if (sign == null)
            {
                SendMessage(user, "Error_NoSign");
                return false;
            }
            // check if user has permission to update this sign
            else if (!sign.CanUpdateSign(user))
            {
                SendMessage(user, "Error_NoSignPermission");
                return false;
            }
            return true;
        }

        /*
         * Removes all user's data who have been inactive longer than 
         * the time specified in the configuration.
         */
        private void PurgeInactive()
        {
            int autoDeleteDaysVIP = config.Misc.AutoDeleteDaysVIP;
            int autoDeleteDays = config.Misc.AutoDeleteDays;
            Puts(Lang("Info_PurgeStart", null, autoDeleteDays, autoDeleteDaysVIP));
            
            int dayLimit = 0;
            List<ulong> usersToRemove = new List<ulong>();

            foreach (KeyValuePair<ulong, UserSaveData> userData in SavedImagesData.UserImages)
            {
                dayLimit = HasAccess(userData.Key.ToString(), PERMISSION_VIP) ? autoDeleteDaysVIP : autoDeleteDays;
                
                if (dayLimit > 0 && ConvertSecondsToDays(userData.Value.TimeLastSeen) > dayLimit)
                {
                    foreach (SavedImageObject savedImage in userData.Value.SavedImages)
                    {
                        ClearFileData(userData.Key, GetFileName(savedImage.IndexID));
                    }
                    userData.Value.SavedImages.Clear();
                    usersToRemove.Add(userData.Key);
                }
            }
            foreach(ulong user in usersToRemove)
            {
                SavedImagesData.UserImages.Remove(user);
                MarkUserFolderForDelete(user);
            }

            Puts(Lang("Info_PurgeEnd", null, usersToRemove.Count()));
        }

        /*
         * Clears the user's image save file.
         */
        private void ClearFileData(string userID, string fileName)
        {
            string path = GetFilePath(userID, fileName);

            if (Interface.Oxide.DataFileSystem.ExistsDatafile(path))
            {
                var CopyData = Interface.Oxide.DataFileSystem.GetDatafile(path);
                CopyData.Clear();
                Interface.Oxide.DataFileSystem.SaveDatafile(path);
            }
        }

        /*
         * Clears the user's image save file.
         */
        private void ClearFileData(ulong userID, string fileName)
        {
            ClearFileData(userID.ToString(), fileName);
        }

        /*
         * Creates folder next to player's empty data folder.
         */
        private void MarkUserFolderForDelete(ulong userID)
        {
            string path = SUB_DIRECTORY + userID.ToString() + "_MARKED/";

            var CopyData = Interface.Oxide.DataFileSystem.GetDatafile(path);
            //CopyData.Clear();
            //Interface.Oxide.DataFileSystem.SaveDatafile(path);
        }

        /*
         * Sets the time the user ran the command.
         */
        private void SetCooldownUsed(Dictionary<ulong, int> userLastUseData, ulong userID, int cooldown)
        {
            if (cooldown != 0)
            {
                if (userLastUseData.ContainsKey(userID))
                    userLastUseData[userID] = (int)GetCurrentTime();
                else
                    userLastUseData.Add(userID, (int)GetCurrentTime());
            }
        }

        /*
         * Checks if enough time has passed since the user last
         * used the command. 
         * 
         * - Returns true when the time passed is shorter than cooldown.
         * - out timeLeft is the amount of time left for the cooldown.
         */
        private bool IsOnCooldown(Dictionary<ulong, int> userLastUseData, ulong userID, int cooldown, out int timeLeft)
        {
            if (cooldown == 0 || !userLastUseData.ContainsKey(userID))
            {
                timeLeft = 0;
                return false;
            }

            // timeleft = cooldown - time since last use
            timeLeft = cooldown - (((int)GetCurrentTime()) - userLastUseData[userID]);

            return timeLeft > 0;
        }

        /*
         * Checks if the enough time has passed since the user last
         * used the command.
         */
        private bool IsOnCooldown(Dictionary<ulong, int> userLastUseData, ulong userID, int cooldown)
        {
            int garbage;
            return IsOnCooldown(userLastUseData, userID, cooldown, out garbage);
        }
        
        /*
         * 
         */
        private void ResizeImageToSign(Signage sign, byte[] imageBytes)
		{	
			if(!SIGN_DETAILS.ContainsKey(sign.ShortPrefabName))
				return;
			
			byte[] resizedImage = ResizeImage(imageBytes,
                                             SIGN_DETAILS[sign.ShortPrefabName].width,
                                             SIGN_DETAILS[sign.ShortPrefabName].height);

			sign.textureID = FileStorage.server.Store(resizedImage, FileStorage.Type.png, sign.net.ID);
		}
		
        /*
         * Resizes the image to the specified width and height.
         */
		private byte[] ResizeImage(byte[] imageBytes, int width, int height)
        {
            Bitmap resizedImage = new Bitmap(width, height);
            Bitmap sourceImage = new Bitmap(new MemoryStream(imageBytes));

            Rectangle currentDimensions = new Rectangle(0, 0, sourceImage.Width, sourceImage.Height);
            Rectangle targetDimensions = new Rectangle(0, 0, width, height);

            System.Drawing.Graphics.FromImage(resizedImage).DrawImage(sourceImage,
                                                                    targetDimensions,
                                                                    currentDimensions,
                                                                    GraphicsUnit.Pixel);         

			MemoryStream ms = new MemoryStream();
			resizedImage.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

			return ms.ToArray(); 
		}
		
        /*
         * Checks if string is an integer.
         */
		private static bool IsInt(string s)
		{
			int x = 0;
			return int.TryParse(s, out x);
		}

        /*
         * Converts string to an integer. 
         * Returns -1 when failed.
         */
        private static int StringToInt(string s)
        {
            int x = -1;
            int.TryParse(s, out x);
            return x;
        }

        /*
         * Checks if user has the specified permission key.
         */
        private bool HasAccess(BasePlayer user, string permKey)
        {
            return user.net.connection.authLevel > 1 || HasAccess(user.UserIDString, permKey);
        }

        /*
         * Checks if user has the specified permission key.
         */
        private bool HasAccess(string userID, string permKey)
        {
            return permission.UserHasPermission(userID, permKey);
        }

        /*
         * Gets the current system time in seconds.
         */
        private static double GetCurrentTime() { return DateTime.UtcNow.Subtract(EPOCH).TotalSeconds; }

        /*
         * Converts the provided seconds to days.
         */
        private static int ConvertSecondsToDays(int seconds) { return seconds / SECONDS_IN_DAY; }

        /*
         * Converts the provided seconds to a user friendly format.
         */
        private string SecondsToReadable(int seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);

            string timeMsg;
            if (t.TotalHours >= 1.0)
                timeMsg = Lang("Misc_Hours", null, Math.Floor(t.TotalHours), t.Minutes);
            else if (t.TotalMinutes >= 1.0)
                timeMsg = Lang("Misc_Minutes", null, t.Minutes, t.Seconds);
            else
                timeMsg = Lang("Misc_Seconds", null, t.Seconds);
            // Years yr, Days d, Hours h, Minutes min, Seconds s
            return timeMsg;
        }

        /*
         * Returns the localized, formatted string indicated by the key.
         */
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        /*
         * Sends a message to the specified user from the provided key.
         */
        private void SendMessage(BasePlayer user, string key, params object[] args) => user.ChatMessage(Lang(key, user.UserIDString, args));

        /*
         * Dimensions and name of sign.
         */
        private class SignDetail
		{
			public int width;
			public int height;
            public string name;
			
			public SignDetail(int width, int height, string name)
			{
				this.width = width;
				this.height = height;
                this.name = name;
			}
		}
		

		private class CustomComparerDictionaryCreationConverter<T> : CustomCreationConverter<IDictionary>
        {
            private readonly IEqualityComparer<T> comparer;

            public CustomComparerDictionaryCreationConverter(IEqualityComparer<T> comparer)
            {
                if (comparer == null)
                    throw new ArgumentNullException(nameof(comparer));
                this.comparer = comparer;
            }

            public override bool CanConvert(Type objectType)
            {
                return HasCompatibleInterface(objectType) && HasCompatibleConstructor(objectType);
            }

            private static bool HasCompatibleInterface(Type objectType)
            {
                return objectType.GetInterfaces().Where(i => HasGenericTypeDefinition(i, typeof(IDictionary<,>))).Any(i => typeof(T).IsAssignableFrom(i.GetGenericArguments().First()));
            }

            private static bool HasGenericTypeDefinition(Type objectType, Type typeDefinition)
            {
                return objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeDefinition;
            }

            private static bool HasCompatibleConstructor(Type objectType)
            {
                return objectType.GetConstructor(new[] {typeof(IEqualityComparer<T>)}) != null;
            }

            public override IDictionary Create(Type objectType)
            {
                return Activator.CreateInstance(objectType, comparer) as IDictionary;
            }
        }
	}
}