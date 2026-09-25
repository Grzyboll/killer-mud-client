# Integracja zmian Grzyboll i laszlowaty — 25.09.2026

## Baza i pochodzenie zmian

Integracja przygotowana dla `tzdziech/killer-mud-client`, na bazie `origin/develop`
`7e6172933ff4e47f79292a807c3ed4d30112add6`. Osobna gałąź:
`codex/integrate-forks-20260925`. Zmiany pozostają niezacommitowane.

Zweryfikowane końce pobranej historii:

- Grzyboll/develop: `b7ea9769546d3f039ac169ad22dc6f567e9f4820`.
- laszlowaty/main: `b5e64405c7dccf2eec2a8c2a2b9a1a6c40241d7e`.

| Zakres | Autor źródłowej implementacji | Commit źródłowy |
|---|---|---|
| 1: skille, sesyjne wykluczenia, usuwanie obszaru | Grzyboll | [141661b](https://github.com/Grzyboll/killer-mud-client/commit/141661bc1211898b5888caf039288ef615ebacd0) |
| 2: ordery leczenia | Grzyboll | [3be6f15](https://github.com/Grzyboll/killer-mud-client/commit/3be6f1570cfec1247a2691f719d51f55a276d529) |
| 3: składnia orderów, autoleczenie, panel | Grzyboll | [9ee1d5e](https://github.com/Grzyboll/killer-mud-client/commit/9ee1d5e8f0966e2f0f03af0aa41d979dd04dbcb3) |
| 4: domemowywanie | Grzyboll | [051ecc5](https://github.com/Grzyboll/killer-mud-client/commit/051ecc54294b041f9d94268cd55be3761adedf7c) |
| 5: ordery odpoczynku | Grzyboll | [098fe43](https://github.com/Grzyboll/killer-mud-client/commit/098fe43f73652b0d334242012a666c8cd9a06dc2) |
| 6: opis stale utrzymywanych buffów | Grzyboll | [8f22206](https://github.com/Grzyboll/killer-mud-client/commit/8f222066e44b41cc1f58a9c46e93b8f6529e6e9d) |
| 9 i 10: przycisk nagrywania, walka podczas obsługi drzwi | Grzyboll | [8a502cc](https://github.com/Grzyboll/killer-mud-client/commit/8a502cc41d5b36e80f7c7d8eea78a232e9c0ff5a) |
| 11: zmiana celu Autofollow podczas marszu | Grzyboll | [c4247cd](https://github.com/Grzyboll/killer-mud-client/commit/c4247cdef68b0973fd5ecf866475253661f2f46b) |
| 12: ślad lidera | Grzyboll | [2a7a223](https://github.com/Grzyboll/killer-mud-client/commit/2a7a223e0308f65cc4d80074afddf820b881d705) |
| 13: schowek znaczników | Grzyboll | [7494616](https://github.com/Grzyboll/killer-mud-client/commit/7494616e9aad6b78efdd39f85d86ca04f6dc3dbc) |
| 14: wspólne znaczniki | Grzyboll | [c0019ff](https://github.com/Grzyboll/killer-mud-client/commit/c0019ffd8fe8509a1db90d260cdcd8900f7375b2) |
| Hot-reload bez restartu timerów | laszlowaty | [5cf0e99](https://github.com/laszlowaty/killer-mud-client/commit/5cf0e99cfe364819c496a1bd7750a355079b3cbd) |
| Ruch z resting bez stand | laszlowaty | [1276498](https://github.com/laszlowaty/killer-mud-client/commit/12764984c2da162936d46423dea7674bbfd40888) |

## Rozstrzygnięcia integracyjne

- Zachowano zmiany użytkownika w obsłudze GMCP: Character Status, statystyki,
  smart buff timers, sprzęt, ekwipunek i odświeżanie Killerpedii. Wywołania nowych
  polityk dodano do istniejących ścieżek; nie zastąpiono handlerów wersjami z forka.
- Nagrywanie: przeniesiono UI z `8a502cc`, ale nie mechanizm z `9d0edac`.
  Przycisk korzysta z `CombatSessionCaptureCoordinator`, `StartTelnetLineCapture`
  i `StopTelnetLineCaptureAsync`, tak samo jak `/capture start` i `/capture stop`.
  Zachowano JSONL Terminal + GMCP, automatyczny start, ścieżkę `CombatCaptures`
  względem bieżących ustawień oraz istniejące reguły zatrzymania i reconnectu.
- Hot-reload: ten fork używa pojedynczych plików JSON profilu i istniejącej
  synchronizacji multibox, a nie katalogów Aliases/Triggers i watchera z forka
  laszlowaty. `ReloadRulesOnly` aktualizuje dodane, zmienione i usunięte reguły
  oraz ich foldery po istniejącym scalaniu trójstronnym. Zachowuje rozstrzygnięcia
  lokalnych edycji oraz działające obiekty timerów i ich tokeny. Usunięto też
  przyczynę zatrzymywania synchronizacji po aktywacji profilu: anulowanie timerów
  użytkownika nie anuluje teraz `system:multibox-sync`.
- `resting` pozwala ruszyć bez `stand`, również na starcie Autofollow. `sitting`
  i `sleeping` uruchamiają recovery i czekają na potwierdzenie wstania. Zachowano
  osobną semantykę świadomego odpoczynku podczas ręcznej trasy oraz czasowej
  regeneracji ruchu: nie jest skracana przez zwykły krok autowalku. Regeneracja
  nadal kończy własny zaplanowany cykl odpoczynku jak wcześniej.
- Order odpoczynku podczas już rozpoczętego Autofollow nie blokuje towarzysza
  bezterminowo. Ręczny autowalk nadal można wstrzymać komendą `rest`.
- Zabezpieczenie przed walką obejmuje zwykłe otwieranie drzwi, recovery po
  timeoutach i sekwencję bramy. Wznowienie po walce/siedzeniu/śnie obsługuje
  także oczekującą bramę. Walka nie stanowi dowodu blokady pokoju.
- Autofollow oznacza własne trasy, więc nie przejmuje ręcznej trasy ani trasy
  farmy. Po walce sprawdza najnowszy cel lidera; powrót lidera do pokoju postaci
  kończy nieaktualną trasę. Ślad jest ograniczony do 300 VNUM-ów i czyszczony po
  utracie/zmianie lidera lub rozłączeniu. Nieciągły ślad uruchamia zwykły pathfinder.
- Autoleczenie włączono do istniejącego mechanizmu `/stop` i `/start`, aby komenda
  awaryjnego zatrzymania nie pozostawiała nowej automatyzacji aktywnej. Dostępne
  jest również `/selfheal`.
- Markery scalono na poziomie zmienionych pól względem rodzica `c0019ff`.
  Wynik: 18 nowych wpisów, 7 zmian istniejących pól, 0 konfliktów. Nie zmieniono
  prywatnego pliku markerów użytkownika ani pozostałej zawartości katalogu.
- Testy dotkniętych obszarów korzystają z wstrzykniętych magazynów tymczasowych,
  żeby nie czytać ani nie modyfikować konfiguracji z AppData działającego klienta.

## Gotowy changelog / release notes

- **Grzyboll — Auto:Farma:** sekwencja umiejętności uruchamiana podczas walki,
  z uwzględnieniem cooldownów GMCP i aktywnych efektów. Prefiks `!` kieruje
  umiejętność na aktualnego przeciwnika. Zablokowane pokoje są pomijane tylko
  w bieżącym uruchomieniu farmy; nie tworzą trwałych znaczników na mapie.
  Można usuwać pojedyncze zaznaczone obszary farmy.
- **Grzyboll — Leczenie i odpoczynek:** ordery leczenia drużyny z poprawną składnią
  `order <postać> cast "<zaklęcie>" self`, autoleczenie poniżej progu HP także
  bez włączonej farmy, opcja „Domemuj, gdy trzeba” oraz ordery odpoczynku przed
  następnym pokojem. Zlecenie leczenia wysyłane jest raz na spadek HP poniżej
  progu. Przy włączonym domemowywaniu `mem` poprzedza każdy order `cast` — klient
  nie zna listy zapamiętanych zaklęć innych członków drużyny.
- **Grzyboll — Buffy farmy:** doprecyzowano istniejące działanie: wpisy bez `!`
  utrzymywane są na postaci w ramach kolejnych kontroli farmy, a ofensywne z `!`
  uruchamiają się po rozpoczęciu walki.
- **Grzyboll — Terminal:** szybki przycisk nagrywania obok „Wyślij”.
  **Adaptacja do tzdziech:** przycisk steruje istniejącym zapisem Terminal + GMCP,
  współdzieli jego stan z komendami `/capture` i automatycznym nagrywaniem;
  nie dodaje drugiego rejestratora ani drugiego formatu plików.
- **Grzyboll — Autowalk i Auto:Farma:** walka wstrzymuje obsługę drzwi, chroni
  przed fałszywym wykluczaniem pokojów i pozwala wznowić przejście po walce.
  **Adaptacja do tzdziech:** objęto ochroną także otwieranie po timeoutach oraz
  wznowienie po wymaganym wstaniu.
- **Grzyboll — Autofollow:** aktualizowanie celu podczas trwającej trasy oraz
  odtwarzanie faktycznego śladu lidera do 300 pokojów, z powrotem do zwykłego
  wyszukiwania trasy, jeśli śladu nie da się połączyć.
  **Integracja:** obsługa nowego celu po walce, powrotu lidera do tego samego
  pokoju i orderu odpoczynku podczas podążania.
- **Grzyboll — Mapa:** Wytnij/Wklej lokalny znacznik wraz z notatką; zawartość
  schowka pozostaje dostępna po wklejeniu. Dodano 18 wspólnych markerów i
  zaktualizowano 7 istniejących pól kategorii/notatek z `c0019ff`.
- **laszlowaty — Automatyzacja:** przeładowywanie aliasów i triggerów bez restartu
  aktywnych timerów. **Adaptacja do tzdziech:** rozwiązanie korzysta z obecnych
  plików JSON i synchronizacji multibox, zachowując lokalne rozstrzygnięcia
  konfliktów; synchronizacja działa również po zmianie profilu.
- **laszlowaty — Autowalk:** `resting` nie wymusza `stand`; `sleeping` i `sitting`
  nadal wymagają wstania. **Integracja:** zgodność z orderami odpoczynku,
  Autofollow, wstrzymaniem ręcznej trasy i istniejącą regeneracją ruchu.
- **Integracja tzdziech:** testy regresyjne współdziałania nowych funkcji,
  zachowanie istniejącego logowania, Character Status, statystyk, smart buff
  timers, Killerpedii oraz inventory/equipment; `/stop` obejmuje nowe autoleczenie.
