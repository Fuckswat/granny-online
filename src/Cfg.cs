using BepInEx.Configuration;
using UnityEngine;

namespace GrannyMP;

public static class Cfg
{
    public static ConfigEntry<string> PlayerName;
    public static ConfigEntry<string> Language;
    public static ConfigEntry<bool> NativeResolution;
    public static ConfigEntry<float> ExecCamBack, ExecCamSide, ExecBodyBack, ExecHeadFrac;
    public static ConfigEntry<string> LastIp;
    public static ConfigEntry<int> Port;

    public static ConfigEntry<KeyCode> MenuKey;
    public static ConfigEntry<KeyCode> TrapKey;
    public static ConfigEntry<KeyCode> ListenKey;
    public static ConfigEntry<KeyCode> CheckKey;
    public static ConfigEntry<KeyCode> BoostKey;
    public static ConfigEntry<KeyCode> PunchKey;
    public static ConfigEntry<KeyCode> UnstuckKey;

    public static ConfigEntry<float> BoostMul, BoostDuration, BoostCooldown;
    public static ConfigEntry<int> PunchChance, PunchCharges;
    public static ConfigEntry<float> PunchDownTime, PunchRange;
    public static ConfigEntry<int> GrannySurviveChance;

    public static ConfigEntry<bool> AllowAiGranny;
    public static ConfigEntry<bool> AdminPanel;
    public static ConfigEntry<int> Days;
    public static ConfigEntry<float> BatCooldown;
    public static ConfigEntry<float> BatRange;
    public static ConfigEntry<float> BatAngle;
    public static ConfigEntry<float> TrapCooldown;
    public static ConfigEntry<int> MaxTraps;
    public static ConfigEntry<float> TrapRadius;
    public static ConfigEntry<float> TrapStunSeconds;
    public static ConfigEntry<float> ListenCooldown;
    public static ConfigEntry<float> ListenDuration;
    public static ConfigEntry<float> GrannyWalkMul;
    public static ConfigEntry<float> GrannyRunMul;
    public static ConfigEntry<float> GrannySpeedMin, GrannySpeedMax, RoleSoundVolume;
    public static ConfigEntry<float> SpiderSpeedMul;
    public static ConfigEntry<float> BiteRange;
    public static ConfigEntry<float> SyncRadius;

    public static ConfigEntry<float> AvatarHeight;
    public static ConfigEntry<float> AvatarYawOffset;
    public static ConfigEntry<float> GrannyBodyBack;
    public static ConfigEntry<bool> AvatarFootsteps;

    public static void Bind(ConfigFile c)
    {
        PlayerName = c.Bind("Сеть", "Имя", Environment.UserName, "Имя игрока, которое видят остальные");
        Language = c.Bind("Интерфейс", "Язык", "auto", "ru, en или auto (по языку системы)");
        NativeResolution = c.Bind("Интерфейс", "Родное разрешение", true, "Запускать игру в разрешении монитора");
        Tr.Init();
        ExecCamBack = c.Bind("Казнь", "Камера: назад", 2.4f, "Гильотина, вид Гренни: отступ камеры от шеи, м");
        ExecCamSide = c.Bind("Казнь", "Камера: вбок", 1.3f, "Гильотина, вид Гренни: сдвиг камеры вбок, м");
        ExecBodyBack = c.Bind("Казнь", "Тело: назад", 0.12f, "Гильотина: тело жертвы за шеей, м");
        ExecHeadFrac = c.Bind("Казнь", "Высота головы", 0.6f, "Гильотина: высота головы манекена в его позе, доля роста");
        LastIp = c.Bind("Сеть", "IP", "127.0.0.1", "IP хоста для подключения (последний введённый)");
        Port = c.Bind("Сеть", "Порт", 7777, "UDP-порт хоста");

        MenuKey = c.Bind("Клавиши", "Меню", KeyCode.F1, "Открыть/закрыть меню мультиплеера");
        TrapKey = c.Bind("Клавиши", "Капкан", KeyCode.G, "Гренни: поставить капкан");
        CheckKey = c.Bind("Клавиши", "Проверка укрытия", KeyCode.Q, "Гренни: заглянуть под кровать или в шкаф");
        ListenKey = c.Bind("Клавиши", "Слух", KeyCode.F, "Гренни: прислушаться (видеть игроков сквозь стены)");
        BoostKey = c.Bind("Клавиши", "Гиперскорость", KeyCode.Q, "Выживший: ускориться");
        UnstuckKey = c.Bind("Клавиши", "Гренни: застряла", KeyCode.U, "Гренни: вытащить себя, если застряла (раз в 15 с)");
        PunchKey = c.Bind("Клавиши", "Удар кулаком", KeyCode.V, "Выживший: ударить Гренни кулаком");

        BoostMul = c.Bind("Выживший", "Гиперскорость: множитель", 2f, "Во сколько раз быстрее");
        BoostDuration = c.Bind("Выживший", "Гиперскорость: длительность", 15f, "Секунд");
        BoostCooldown = c.Bind("Выживший", "Гиперскорость: перезарядка", 60f, "Секунд после окончания");
        PunchChance = c.Bind("Выживший", "Кулаки: шанс", 15, "Шанс свалить Гренни одним ударом, %");
        PunchCharges = c.Bind("Выживший", "Кулаки: ударов за раунд", 3, "Сколько раз за раунд можно ударить Гренни");
        PunchDownTime = c.Bind("Выживший", "Кулаки: Гренни лежит", 10f, "Сколько секунд Гренни лежит после удачного удара");
        GrannySurviveChance = c.Bind("Гренни", "Шанс выжить после оружия", 10, "Шанс, что выстрел/стрела/взрыв/машина Гренни не свалят, %");
        PunchRange = c.Bind("Выживший", "Кулаки: дальность", 4.2f, "В единицах мира (как дальность биты)");

        AllowAiGranny = c.Bind("Игра", "ИИ-Гренни в жеребьёвке", true, "Если да — при раздаче ролей может выпасть, что Гренни управляет компьютер (все игроки выживают)");
        AdminPanel = c.Bind("Игра", "Админ-панель", true, "Показывать хосту админ-панель для тестов (F2-F7)");
        Days = c.Bind("Игра", "Дней", 5, "Сколько раз выжившего можно поймать (дней) до выбывания");
        BatCooldown = c.Bind("Гренни", "Перезарядка биты", 1.2f, "Секунд между ударами");
        BatRange = c.Bind("Гренни", "Дальность биты", 3.4f, "В единицах мира (рост Гренни ~4)");
        BatAngle = c.Bind("Гренни", "Угол удара", 80f, "Ширина конуса удара в градусах");
        TrapCooldown = c.Bind("Гренни", "Перезарядка капкана", 15f, "Секунд между капканами");
        MaxTraps = c.Bind("Гренни", "Макс. капканов", 6, "Сколько капканов может стоять одновременно");
        TrapRadius = c.Bind("Гренни", "Радиус капкана", 1.1f, "Насколько близко надо наступить");
        TrapStunSeconds = c.Bind("Гренни", "Время в капкане", 5f, "Сколько секунд выживший не может двигаться");
        ListenCooldown = c.Bind("Гренни", "Перезарядка слуха", 45f, "Секунд между прислушиваниями");
        ListenDuration = c.Bind("Гренни", "Длительность слуха", 3f, "Сколько секунд игроки подсвечены розовым");
        GrannyWalkMul = c.Bind("Гренни", "Скорость шага", 1.0f, "Множитель скорости ходьбы Гренни относительно выжившего");
        GrannyRunMul = c.Bind("Гренни", "Скорость бега", 0.85f, "Множитель скорости бега Гренни относительно бега выжившего");
        GrannySpeedMin = c.Bind("Гренни", "Случайная скорость: от", 0.8f, "Каждый раунд хост выбирает скорость Гренни случайно: доля от скорости выжившего. Гренни всегда медленнее выжившего (не больше 0.95)");
        GrannySpeedMax = c.Bind("Гренни", "Случайная скорость: до", 0.95f, "Верхняя граница случайной скорости Гренни (больше 0.95 не бывает)");
        RoleSoundVolume = c.Bind("Интерфейс", "Громкость музыки роли", 0.4f, "Музыка на карточке роли в начале раунда, 0..1 (от громкости игры)");
        SpiderSpeedMul = c.Bind("Анжела", "Скорость", 1.0f, "Множитель скорости паучихи относительно бега выжившего");
        BiteRange = c.Bind("Анжела", "Дальность укуса", 3.0f, "В единицах мира");
        SyncRadius = c.Bind("Синхронизация", "Радиус", 9f, "Предметы/двери синхронизирует игрок, который рядом с ними");

        AvatarHeight = c.Bind("Модель", "Рост", 0f, "Рост модели игрока в единицах мира (0 = автоматически по камере)");
        AvatarYawOffset = c.Bind("Модель", "Поворот", 0f, "Доп. поворот модели в градусах (если модель смотрит не туда — поставь 180)");
        GrannyBodyBack = c.Bind("Модель", "Гренни: тело за камерой", 0.28f, "На сколько тело Гренни сдвинуто назад от камеры (вид от первого лица)");
        AvatarFootsteps = c.Bind("Модель", "Шаги", true, "Проигрывать звук шагов других выживших (Гренни их слышит)");
    }
}
