# Local AI Toolkit

![Логотип Local AI](images/localai-mark.png)

**Меньше затрат. Быстрее к результату.**

[English version](README.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/MrAliev/local-ai-toolkit/blob/main/LICENSE)

[Руководство по проекту](opensource.ru.md) · [Интерактивный сайт](https://mraliev.github.io/local-ai-toolkit/?lang=ru) · [Wiki](https://github.com/MrAliev/local-ai-toolkit/wiki) · [Поддержка](support.ru.md)

Сокращайте платный облачный контекст и быстрее находите нужный код. Local AI ищет по
смыслу и символам в готовом индексе репозитория и поручает локальным моделям разбор файлов,
журналов и изображений. Ваш агент получает краткие выводы и координаты источников вместо
лишнего исходного текста.

Экономия зависит от контекста, действительно необходимого запросу, с учётом накладных
расходов инструментов. Время ответа зависит от задачи, индекса, очереди и оборудования;
локальные вычисления не делают каждую задачу автоматически быстрее.

Облачная подписка на какого-либо AI-агента при этом не обязательна: те же инструменты отвечают из командной строки, если Вы предпочитаете работать самостоятельно.

---

## Оглавление

- [Два способа использования](two-ways-to-use-it.ru.md)
- [Что доступно облачному агенту](what-a-cloud-agent-gets.ru.md)
- [Что доступно из командной строки](what-the-command-line-gets.ru.md)
- [Поиск по коду — не текстовый поиск](code-search-is-not-text-search.ru.md)
- [С облачным агентом: на что теперь не тратятся токены](with-a-cloud-agent-what-stops-costing-tokens.ru.md)
  - [Оценка контекста одной операции](with-a-cloud-agent-what-stops-costing-tokens.ru.md#оценка-контекста-одной-операции)
  - [Это работает по умолчанию, и Вы это видите](with-a-cloud-agent-what-stops-costing-tokens.ru.md#это-работает-по-умолчанию-и-вы-это-видите)
  - [Если что-то работает не так, как Вам нужно](with-a-cloud-agent-what-stops-costing-tokens.ru.md#если-что-то-работает-не-так-как-вам-нужно)
- [Из консоли: не про токены, а про время](from-the-command-line-not-tokens-but-time.ru.md)
- [Как это устроено](how-it-works.ru.md)
  - [Из чего состоит](how-it-works.ru.md#из-чего-состоит)
  - [Одна очередь перед всеми моделями](how-it-works.ru.md#одна-очередь-перед-всеми-моделями)
  - [Индекс кода](how-it-works.ru.md#индекс-кода)
  - [Поиск по шагам](how-it-works.ru.md#поиск-по-шагам)
  - [Какая модель что делает](how-it-works.ru.md#какая-модель-что-делает)
  - [Как считается экономия](how-it-works.ru.md#как-считается-экономия)
  - [Что делает установщик](how-it-works.ru.md#что-делает-установщик)
- [Зависимости](dependencies.ru.md)
- [Как установить](installing.ru.md)
  - [На чём это будет считать](installing.ru.md#на-чём-это-будет-считать)
- [Как пользоваться с облачным агентом](using-it-with-a-cloud-agent.ru.md)
- [Как пользоваться из консоли](using-it-from-the-command-line.ru.md)
  - [Шаг 1. Как вообще запускать](using-it-from-the-command-line.ru.md#шаг-1-как-вообще-запускать)
  - [Шаг 2. Откуда запускать](using-it-from-the-command-line.ru.md#шаг-2-откуда-запускать)
  - [Шаг 3. Подключить репозиторий](using-it-from-the-command-line.ru.md#шаг-3-подключить-репозиторий)
  - [Шаг 4. Пользоваться](using-it-from-the-command-line.ru.md#шаг-4-пользоваться)
- [На каком языке он отвечает](what-language-it-answers-in.ru.md)
- [Обновление и удаление](updating-and-removing.ru.md)
  - [Обновить](updating-and-removing.ru.md#обновить)
  - [Удалить устаревшие данные, но оставить продукт](updating-and-removing.ru.md#удалить-устаревшие-данные-но-оставить-продукт)
  - [Удалить целиком](updating-and-removing.ru.md#удалить-целиком)
- [Данные для анализа влияния: пилот C#](impact-evidence-c-pilot.ru.md)
- [Пакеты документации](documentation-packages.ru.md)
- [Собрать и проверить](building-and-testing.ru.md)
  - [Независимые корпуса для проверки качества](building-and-testing.ru.md#независимые-корпуса-для-проверки-качества)
- [Лицензия](licence.ru.md)

<!-- Generated from the README by DocumentationSiteTests. -->
