"use strict";
(() => {
  const ru = {
    skip:"К содержимому",docs:"Документация",eyebrow:"ОТКРЫТЫЙ КОД · WINDOWS · MCP",
    hero:"Меньше затрат.<br><em>Быстрее к результату.</em>",lead:"Сокращайте платный облачный контекст с помощью локального поиска и анализа. Находите нужный код в индексе, разбирайте файлы на своём ПК и передавайте агенту краткие выводы.",start:"Начать →",explore:"Изучить архитектуру",heroNote:"Лицензия MIT. Windows · MCP · CLI. Экономия и время ответа зависят от задачи, индекса и оборудования.",repository:"Ваш репозиторий",semantic:"поиск по смыслу и символам",broker:"Общий брокер",queue:"одна очередь моделей",evidence:"Доказательства для агента",p1tag:"01 / ЗАТРАТЫ",p1:"Меньше облачного контекста",p1text:"Поручайте первое чтение локальным инструментам и получайте нужные выдержки. Это помогает сократить отправку лишнего текста в облако.",p2tag:"02 / СКОРОСТЬ",p2:"Быстрее к нужному коду",p2text:"Ищите по смыслу или символам в готовом индексе. Координаты источников позволяют сразу перейти к следующему шагу.",p3tag:"03 / ЯСНОСТЬ",p3:"Понятный результат",p3text:"Отчёт различает оценку экономии, дополнительные накладные расходы и неизвестный результат.",inside:"УСТРОЙСТВО СИСТЕМЫ",architecture:"Проследите путь задачи.",architectureLead:"Три представления одной системы. Выберите этап, чтобы увидеть его назначение и границы.",requestTab:"Запрос",indexTab:"Жизненный цикл индекса",trustTab:"Доверие к релизу",flowNote:"Это схема, а не текущий статус. Маршрут подписи включает запланированный этап SignPath.",fullArchitecture:"Полная архитектура →",estimateLabel:"РАЗОБРАТЬСЯ В ОЦЕНКЕ",estimateTitle:"Меньше контекста.<br>Прозрачный расчёт.",estimateText:"Сравнивайте с контекстом, необходимым для запроса, а не с суммой размеров всех файлов, прочитанных локально.",methodology:"Методика расчёта →",localOnly:"Учебный пример. Все расчёты выполняются на странице. Без вызова модели, аналитики и данных аккаунта.",sourceLabel:"Необходимый новый контекст (S)",responseLabel:"Полный возвращённый ответ (V)",requestLabel:"Запрос к инструменту (R)",knownLabel:"Я явно предполагаю, что это необходимый контекст задачи.",seenLabel:"Облачный агент уже прочитал этот контекст.",formulaNote:"S − V − 2R. Запрос генерируется, а затем читается в контексте. Локальная генерация автоматически не засчитывается. Это не счёт и не замер экономии.",handbook:"РУКОВОДСТВО",next:"Понятный следующий шаг.",nextText:"Начните с поддерживаемого сценария, затем переходите к деталям.",install:"Установить и подключить",installText:"Требования, параметры установщика и первый репозиторий.",operate:"Работать и проверять",operateText:"Команды CLI, состояние индекса и практические проверки.",privacy:"Данные и доверие",privacyText:"Что хранится локально, что получают клиенты и как проверяется релиз.",contribute:"Собирать и улучшать",contributeText:"Границы проекта, управление, поддержка и участие в разработке.",questions:"Перед началом работы.",faq1:"Локальная модель означает, что ничто не попадает в облако?",faq1text:"Нет. Вызывающий клиент получает ответы и выбранные фрагменты; облачный агент может отправить их провайдеру. Точные границы описаны в политике конфиденциальности.",faq2:"Каждый вызов экономит токены?",faq2text:"Нет. Если источник уже в контексте или запрос и ответ дороже необходимой альтернативы, делегирование добавляет расходы. Без достаточных доказательств оценка неизвестна.",faq3:"Программы Windows уже подписаны Authenticode?",faq3text:"Не следует этого предполагать. Сейчас используется манифест с ECDSA-подписью. До объявления подписанного Authenticode релиза нужны одобрение SignPath и проверенный маршрут CI-подписи.",faq4:"Можно использовать Local AI для нескольких проектов сразу?",faq4text:"Да. Они разделяют брокер и очередь моделей. Проверяйте статус и согласовывайте длительные операции; не останавливайте общий runtime и не выгружайте модель другой сессии при диагностике своего проекта.",footer:"Открытый код под MIT. У зависимостей и моделей собственные лицензии.",signing:"Политика подписи кода",privacyShort:"Конфиденциальность",support:"Поддержка"
  };
  const views = {
    request:[
      ["Client","Клиент","User intent","Намерение пользователя","A client sends a bounded task or a search query. Results can be forwarded to its cloud provider.","Клиент отправляет ограниченную задачу или поисковый запрос. Полученные результаты могут передаваться его облачному провайдеру."],
      ["Tools & index","Инструменты и индекс","Repository context","Контекст репозитория","CodeSearch reads the matching base and worktree overlay. LocalLm handles known files, logs, images or translation.","CodeSearch читает подходящую базу и overlay рабочего дерева. LocalLm работает с известными файлами, журналами, изображениями и переводами."],
      ["Shared broker","Общий брокер","Resource boundary","Граница ресурсов","All model inference passes through the durable shared queue and configured residency policy.","Все модельные вычисления проходят через общую долговечную очередь и настроенную политику размещения."],
      ["Evidence","Доказательства","Returned to client","Возвращаются клиенту","The client receives an answer, source locations and a usage notice. Verify consequential claims against focused evidence.","Клиент получает ответ, координаты источников и отчёт об использовании. Важные выводы проверяются по конкретным доказательствам."]
    ],
    index:[
      ["Connect","Подключить","Explicit opt-in","Явное подключение","Connect a repository deliberately. Status distinguishes an unconfigured repository from a usable index.","Репозиторий подключается явно. Статус отличает неподключённый репозиторий от готового индекса."],
      ["Build","Построить","Progress by phase","Прогресс по этапам","Snapshot, analysis, chunking and embeddings form an index. Known totals get counts; unknown work must not get an invented percentage.","Снимок, анализ, разбиение и embeddings формируют индекс. Известные объёмы дают счётчики; неизвестным нельзя приписывать выдуманный процент."],
      ["Refresh","Обновлять","Worktree state","Состояние worktree","Saved edits and branch changes need a matching overlay. Refresh the working tree before searching changed code.","Сохранённым изменениям и другой ветке нужен соответствующий overlay. Перед поиском изменённого кода обновите индекс рабочего дерева."],
      ["Inspect","Проверять","Status before assumptions","Статус вместо догадок","Inspect freshness and active sync before starting another build. Unload releases the memory cache, preserving the on-disk index.","Проверьте свежесть и активную синхронизацию до запуска новой сборки. Unload освобождает кэш в памяти, сохраняя индекс на диске."]
    ],
    trust:[
      ["Reviewed source","Проверенный код","Source commit","Коммит исходников","A focused PR and passing checks establish the reviewed source. Signing approval is a separate human decision.","Отдельный PR и успешные проверки фиксируют проверенный исходный код. Одобрение подписи — отдельное решение человека."],
      ["CI build","Сборка CI","Required for SignPath","Требуется для SignPath","SignPath needs a verifiable build artifact from the trusted CI workflow. An attestation of a later upload is not the same evidence.","SignPath нужен проверяемый артефакт доверенного CI workflow. Аттестация позднее загруженных файлов не заменяет это доказательство."],
      ["Authenticode","Authenticode","Planned · approval needed","Планируется · нужно одобрение","After onboarding, sign only project-owned binaries through SignPath and verify the real publisher policy. No certificate is claimed here.","После подключения подписывайте через SignPath только собственные программы и проверяйте реальную политику издателя. Эта страница не заявляет о полученном сертификате."],
      ["Final package","Итоговый пакет","Manifest and verification","Манифест и проверка","Package signed inner binaries first, then hash and sign the final manifest. Verify before publishing; do not mutate the package afterward.","Сначала упакуйте подписанные программы, затем вычислите хеш и подпишите итоговый манифест. Проверьте до публикации; далее пакет нельзя изменять."]
    ]
  };
  const english = new Map([...document.querySelectorAll("[data-i18n]")].map(e => [e,e.innerHTML]));
  const internalLinks = new Map([...document.querySelectorAll('a[href$=".html"]')].map(e => [e,e.getAttribute("href")]));
  let lang = new URLSearchParams(location.search).get("lang") === "ru" ? "ru" : "en";
  let view = "request", stage = 0;
  const $ = id => document.getElementById(id);
  function renderFlow() {
    const restoreFocus = $("flow").contains(document.activeElement);
    $("flow").replaceChildren();
    views[view].forEach((item,i) => {
      const button = document.createElement("button"); button.type = "button";
      button.className = i === stage ? "active" : ""; button.setAttribute("aria-pressed",String(i === stage));
      const num = document.createElement("span");num.textContent = `0${i+1} →`;
      button.append(num,document.createTextNode(item[lang === "ru" ? 1 : 0]));
      button.addEventListener("click",() => {stage=i;renderFlow();});$("flow").append(button);
    });
    const item=views[view][stage], badge=document.createElement("span"),title=document.createElement("h3"),text=document.createElement("p");
    badge.className="badge";badge.textContent=item[lang === "ru"?3:2];title.textContent=item[lang === "ru"?1:0];text.textContent=item[lang === "ru"?5:4];
    $("stage-detail").replaceChildren(badge,title,text);
    if (restoreFocus) $("flow").children[stage].focus({preventScroll:true});
  }
  function calculate() {
    const fields=["source","response","request"].map($);
    if(fields.some(e=>!e.validity.valid || e.value.trim()==="")) {
      $("result-label").textContent=lang==="ru"?"Проверьте исходные данные":"Check the inputs";$("result-value").textContent="—";$("result-detail").textContent=lang==="ru"?"Нужны целые числа от 0 до 10 000 000.":"Use whole numbers from 0 to 10,000,000.";return;
    }
    if(!$("known").checked && !$("seen").checked) {
      $("result-label").textContent=lang==="ru"?"Оценить нельзя":"Cannot estimate";$("result-value").textContent="—";$("result-detail").textContent=lang==="ru"?"Необходимый альтернативный контекст не установлен.":"The necessary alternative context is not established.";return;
    }
    const [source,response,request]=fields.map(e=>Number(e.value));
    const saving=($("seen").checked?0:source)-response-2*request;
    $("result-label").textContent=saving>0?(lang==="ru"?"Условно сэкономлено примерно токенов":"Conditional estimate: tokens saved"):(lang==="ru"?"Экономии нет; дополнительно токенов":"No savings; additional tokens");
    $("result-value").textContent=new Intl.NumberFormat(lang).format(Math.abs(saving));
    $("result-detail").textContent=lang==="ru"?"Учебный сценарий, не фактическая телеметрия и не расход аккаунта.":"An illustrative scenario, not live telemetry or account usage.";
  }
  function renderLanguage() {
    document.documentElement.lang=lang;
    english.forEach((text,e)=>{e.innerHTML=lang==="ru"?(ru[e.dataset.i18n]||text):text;});
    internalLinks.forEach((href,e)=>e.setAttribute("href",lang==="ru"?href.replace(/\.html$/,".ru.html"):href));
    $("language").textContent=lang==="ru"?"English":"Русский";$("language").lang=lang==="ru"?"en":"ru";$("language").setAttribute("aria-label",lang==="ru"?"Switch to English":"Переключить на русский");
    document.title=lang==="ru"?"Local AI Toolkit · Меньше затрат. Быстрее к результату.":"Local AI Toolkit · Lower costs. Faster results.";
    renderFlow();calculate();
  }
  $("language").addEventListener("click",()=>{lang=lang==="ru"?"en":"ru";const url=new URL(location.href);url.searchParams.set("lang",lang);history.replaceState(null,"",url);renderLanguage();});
  document.querySelectorAll("[data-view]").forEach(button=>button.addEventListener("click",()=>{
    view=button.dataset.view;stage=0;document.querySelectorAll("[data-view]").forEach(b=>{b.classList.toggle("active",b===button);b.setAttribute("aria-pressed",String(b===button));});renderFlow();
  }));
  $("calculator").addEventListener("submit",e=>e.preventDefault());$("calculator").addEventListener("input",calculate);
  renderLanguage();
})();
