## What this changes / Что меняется

<!-- One subject per pull request. / Одна тема на pull request. -->

## Why / Зачем

<!-- The failure or the need behind it. / Поломка или потребность, из-за которой это появилось. -->

## Checklist / Проверка

- [ ] Branched from `main`, one subject / ветка от `main`, одна тема
- [ ] Tests accompany the behaviour change, and none assert how fast the machine is /
      тесты идут вместе с поведением и не проверяют скорость машины
- [ ] `dotnet test LocalAi.slnx --configuration Release` passes locally / проходит локально

<!-- CI is off for now: the full suite is run locally before opening a pull request. /
     CI пока выключен: полный прогон выполняется локально перед открытием pull request. -->
