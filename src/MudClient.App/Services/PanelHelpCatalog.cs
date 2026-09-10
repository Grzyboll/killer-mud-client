using MudClient.App.Models;

namespace MudClient.App.Services;

/// <summary>
/// Single source of truth for the short help shown both next to panels and in the central Help
/// window. Keeping the copy here prevents docked panels, Terminal overlays and full Help from
/// drifting apart.
/// </summary>
public static class PanelHelpCatalog
{
    public static IReadOnlyList<PanelHelpTopic> All { get; } =
    [
        new(
            "Terminal",
            "Terminal",
            "Główne okno komunikacji z MUD-em. Pokazuje tekst odebrany z gry, pozwala wpisywać komendy oraz prezentuje skrócony stan postaci, aktywnych timerów, triggerów, czasu świata i pogody.",
            [
                "HP i MV pokazują aktualne oraz maksymalne wartości.",
                "Pasek timerów pozwala je wstrzymać, wznowić lub uruchomić od początku.",
                "Wyszukiwarka przeszukuje tekst znajdujący się w bieżącym buforze terminala."
            ],
            "Wygląd terminala, czcionkę, kolory ANSI, czyszczenie pola komendy i separator wielu komend zmienisz w panelu Ustawienia.",
            [
                "Enter — wyślij komendę lub zaakceptuj podpowiedź; Shift+Enter — wstaw nową linię.",
                "Strzałki góra/dół — wybierz podpowiedź albo przejdź po historii komend; Tab — zaakceptuj podpowiedź.",
                "W wyszukiwarce: Enter — poprzedni wynik; Shift+Enter — następny wynik."
            ]),
        new(
            "Group",
            "Drużyna",
            "Panel pokazuje członków Twojej drużyny, ich stan zdrowia, poziom ruchu, pozycję oraz aktualne położenie. Przyciski pod postacią używają wybranego zaklęcia lub umiejętności na tej osobie.",
            ["Zielone kropki nad przyciskiem zaklęcia pokazują liczbę jego zapamiętanych użyć."],
            "Przez ⚙ możesz dodawać i usuwać własne przyciski. Podaj pełną nazwę zaklęcia lub umiejętności oraz krótki napis widoczny na przycisku.",
            []),
        new(
            "MemSpells",
            "Stan postaci",
            "Panel pokazuje kondycję, własne buffy, pozostałe efekty oraz zwijaną listę zapamiętanych zaklęć.",
            [
                "[+] — buff jest aktywny; [-] — buffa brakuje.",
                "[2/1] — 2 użycia są zapamiętane, a 1 zostało wykorzystane.",
                "Kliknięcie buffa rzuca go, jeśli zaklęcie jest dostępne. RZUĆ BRAKUJĄCE rzuca dostępne zaklęcia, których efektów brakuje."
            ],
            "Przez ⚙ możesz tworzyć zestawy, zmieniać ich nazwy, usuwać je, dodawać śledzone zaklęcia oraz ustawić liczbę kolumn wyłącznie dla własnych buffów. Pojedynczy buff usuwa się przyciskiem ✕ w panelu.",
            []),
        new(
            "OffensiveActions",
            "Offensywne i Definiowalne",
            "Offensywne to szybkie przyciski zaklęć i umiejętności, używane głównie podczas walki. Definiowalne wykonują wpisane przez Ciebie polecenia.",
            [
                "Zielone kropki pokazują liczbę zapamiętanych użyć zaklęcia.",
                "Czerwone ★ oznaczają, że umiejętność nie jest jeszcze ponownie gotowa."
            ],
            "Przez ⚙ możesz dodawać i usuwać przyciski, ustawić osobną liczbę przycisków w rzędzie oraz ułożyć obie sekcje pionowo albo obok siebie.",
            []),
        new(
            "Automation",
            "Automaty",
            "Panel służy do tworzenia timerów, aliasów i triggerów. Automaty mogą wykonywać komendy MUD-a albo skrypty Lua.",
            [
                "Globalny oznacza wpis wspólny dla wszystkich kont; Lua — skrypt zamiast listy komend; 🔔 — dźwięk przy uruchomieniu.",
                "Aliasy reagują na wpisane komendy, triggery na linie z MUD-a, a timery uruchamiają się cyklicznie.",
                "Wzorce aliasów i triggerów używają wyrażeń regularnych .NET. $1, $2 itd. w akcji odnoszą się do przechwyconych grup.",
                "Test skryptu Lua naprawdę go uruchamia, więc zmiany jego zmiennych i liczników pozostają zapisane."
            ],
            "Możesz tworzyć foldery, przełączać widok kompaktowy, importować i eksportować JSON oraz osobno włączać lub wyłączać wpisy.",
            []),
        new(
            "AutomationTeam",
            "Auto: Drużyna",
            "Panel zawiera automatyczne reakcje związane z członkami aktualnej drużyny: wspieranie walki, wykonywanie rozkazów, zdalne sterowanie i reakcję na gest lidera.",
            [
                "Autoassist reaguje na członka drużyny walczącego w tym samym pokoju; {cel} jest zastępowane nazwą jego przeciwnika.",
                "Autoassist NPC wysyła rozkaz własnym NPC znajdującym się w grupie.",
                "Rozkazy są wykonywane tylko od członka aktualnej grupy, a zdalne polecenia z ! tylko od wskazanej postaci.",
                "Większość funkcji wymaga aktualnych danych drużyny z GMCP."
            ],
            "Każdą funkcję możesz włączyć osobno oraz ustawić jej komendy, wyjątki i nazwę uprawnionej postaci.",
            []),
        new(
            "AutomationTravel",
            "Auto: Podróż",
            "Panel steruje odzyskiwaniem ruchu podczas autowalku oraz zachowaniem drużyny w trakcie podróży.",
            [
                "Po osiągnięciu progu MV klient próbuje rzucić zapamiętany refresh.",
                "Jeśli refresh nie jest dostępny, postać odpoczywa przez ustawiony czas, a następnie wznawia trasę.",
                "Autofollow nie uruchamia się podczas walki ani innej aktywnej podróży."
            ],
            "Możesz zmienić próg MV, czas odpoczynku, odpoczynek po dotarciu oraz reakcje lidera i członków drużyny.",
            []),
        new(
            "AutomationCombat",
            "Auto: Walka",
            "Panel zawiera wbudowane reakcje na konkretne sytuacje bojowe. Nie są to zwykłe triggery i dlatego nie pojawiają się na liście w panelu Automaty.",
            [
                "Autostand wysyła stand, gdy GMCP lub komunikat gry wskazuje powalenie postaci.",
                "Autowield po rozbrojeniu próbuje podnieść, a następnie założyć wskazaną broń."
            ],
            "Obie reakcje możesz włączać niezależnie. Dla Autowield podaj nazwę używaną w komendach get i wield.",
            []),
        new(
            "AutomationFarm",
            "Auto: Farma",
            "Panel odwiedza nieodkryte pokoje zaznaczonego obszaru, może atakować wskazane moby oraz zatrzymywać marsz na leczenie, zapamiętywanie zaklęć i odpoczynek.",
            [
                "Farma wymaga obszaru zaznaczonego w ustawieniach mapy i zatrzymuje chodzenie po osiągnięciu ustawionego progu HP.",
                "Zaklęcia leczące są wybierane od góry listy, od najsilniejszego dostępnego.",
                "~zaklęcie jest zapamiętywane tylko przy okazji innego postoju; !zaklęcie jest ofensywne i kierowane w aktualnego przeciwnika.",
                "Autokill atakuje tylko wskazane moby rzeczywiście obecne w pokoju."
            ],
            "Możesz ustawić próg HP, opóźnienie między pokojami, listy zaklęć i cele Autokilla. Czas odpoczynku pochodzi z panelu Auto: Podróż.",
            []),
        new(
            "Notes",
            "Notatki",
            "Panel pozwala zapisywać własne informacje w folderach, osobno dla konta albo globalnie dla wszystkich kont.",
            [
                "Etykieta Globalna oznacza notatkę wspólną dla wszystkich kont.",
                "✎ otwiera edycję, ✕ usuwa notatkę, a foldery służą do organizowania wpisów."
            ],
            "Podczas tworzenia lub edycji możesz zmienić tytuł, treść, folder i zakres globalny.",
            []),
        new(
            "Gmcp",
            "GMCP",
            "Panel diagnostyczny pokazujący surowe komunikaty GMCP odebrane z serwera oraz wysłane przez klienta.",
            [
                "Odebrane zawiera pakiety przesłane przez MUD, a Wysłane — pakiety wysłane przez klienta.",
                "Każdy wpis pokazuje nazwę pakietu, czas i dane JSON."
            ],
            "Panel nie zmienia obsługi GMCP. Służy do diagnostyki, tworzenia automatyzacji i sprawdzania danych udostępnianych przez serwer.",
            []),
        new(
            "Chat",
            "Czat",
            "Panel wydziela komunikację graczy z głównego tekstu terminala, aby łatwiej śledzić rozmowy.",
            [
                "Panel obejmuje rozpoznane linie say, tell, clantell, grouptell, yell i shout.",
                "Wiadomości pozostają również w historii terminala."
            ],
            "Przez ⚙ możesz włączyć krótki dźwięk systemowy przy każdej nowej rozpoznanej wiadomości.",
            []),
        new(
            "Settings",
            "Ustawienia",
            "Panel zawiera ustawienia wyglądu i działania aplikacji, aktualizacji, danych gry oraz kopii konfiguracji.",
            [
                "Ustawienia terminala i pozostałych paneli są niezależne, a przezroczystość dotyczy wszystkich nakładek na terminal.",
                "Separator komend działa również w aliasach, triggerach i timerach.",
                "Aktualizacja aplikacji i aktualizacja danych gry to dwie niezależne operacje.",
                "Eksport ZIP obejmuje cały katalog danych aplikacji, w tym konta, profile, automatyzację i układ paneli."
            ],
            "Większość zmian zapisuje się automatycznie. Import ZIP zastępuje dane ustawień zawartością wybranego archiwum.",
            []),
        new(
            "Map",
            "Ruch",
            "Przyciski N / S / W / E / U / D służą do poruszania postacią. Dostępne wyjścia są aktywne, a pozostałe kierunki pozostają widoczne, lecz są wyłączone.",
            ["🔒 oznacza zamknięte przejście. Kliknij je normalnie, a klient spróbuje je otworzyć."],
            "Ustawienia mapy pod ⚙ nie zmieniają przypisania klawiszy ruchu.",
            [
                "NumPad 8 — północ; 2 — południe; 4 — zachód; 6 — wschód.",
                "NumPad 9 — góra; 3 — dół."
            ])
    ];

    public static PanelHelpTopic? Find(string? panelId) =>
        All.FirstOrDefault(topic => string.Equals(topic.PanelId, panelId, StringComparison.Ordinal));
}
