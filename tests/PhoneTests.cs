using SafePaste.Detecting;

namespace SafePaste.Tests
{
    /// <summary>Номера телефонов: российские и международные форматы, подсказки, ложные срабатывания.</summary>
    public static partial class TestProgram
    {
        private static void PhoneTests()
        {
            Section("Телефоны");
            Hides("+7 с пробелами", "Звонил +7 916 123-45-67 вчера", "916 123-45-67");
            Hides("+7 со скобками", "Мой +7 (916) 123-45-67", "123-45-67");
            Hides("+7 слитно", "+79161234567", "9161234567");
            Hides("+7 без пробелов со скобками", "+7(916)1234567", "1234567");
            Hides("8 со скобками", "Иван 8 (916) 123-45-67", "123-45-67");
            Hides("8 без пробела перед скобкой", "8(916)123-45-67", "123-45-67");
            Hides("8 через дефисы", "8-916-123-45-67", "916-123-45-67");
            Hides("8 800", "Горячая линия 8 800 555 35 35", "555 35 35");
            Hides("региональный код", "8 (8452) 12-34-56", "12-34-56");
            Hides("11 цифр слитно", "Позвони Ивану на 89161234567 завтра", "89161234567");
            Hides("два номера через пробел", "89161234567 89161234568", "89161234567", "89161234568");
            Hides("код в скобках без восьмёрки", "Офис (495) 123-45-67", "123-45-67");
            Hides("мобильный без восьмёрки", "Иван: 916 123-45-67", "123-45-67");
            Hides("международный", "London +44 20 7946 0958", "7946 0958");
            Hides("США", "Office +1 (202) 555-0147", "555-0147");
            Hides("неразрывные пробелы", "+7" + (char)0x00A0 + "916" + (char)0x00A0 + "123-45-67", "123-45-67");

            Hides("короткий после тел.", "тел. 22-33-44", "22-33-44");
            Hides("короткий после Телефон:", "Телефон: 123-45-67", "123-45-67");
            Hides("после по номеру", "Звоните по номеру 123-45-67", "123-45-67");
            Hides("факс", "Тел./факс: 123-45-67", "123-45-67");
            Hides("ключ JSON", "{\"mobilePhone\": \"5551234\"}", "5551234");
            Hides("ключ env", "CONTACT_PHONE=5551234", "5551234");
            Hides("ссылка tel:", "<a href=\"tel:+79161234567\">", "9161234567");
            Hides("список номеров", "тел. 123-45-67, 234-56-78", "123-45-67", "234-56-78");
            Hides("повтор без подсказки", "тел. 123-45-67\r\nНомер 123-45-67 занят", "123-45-67");

            HidesQuick("+7 в быстрой вставке", "Звонил +7 916 123-45-67", "123-45-67");
            HidesQuick("8 (916) в быстрой вставке", "8 (916) 123-45-67", "123-45-67");
            HidesQuick("подсказка в быстрой вставке", "моб.: 89161234567", "89161234567");
            HidesQuick("короткий в быстрой вставке", "Телефон: 123-45-67", "123-45-67");
            HidesQuick("международный в быстрой вставке", "Контакт: +1-202-555-0147", "555-0147");
            KeepsQuick("11 цифр без подсказки остаются в быстрой вставке", "Заказ 89161234567 оплачен", "89161234567");
            FoundButKept("номер как у США без кода", "Артикул 202-555-0147", "202-555-0147");
            FoundButKept("догадка не делает соседа надёжным", "Артикулы 202-555-0147, 202-555-0148", "202-555-0148");
            HidesMode("строгий режим скрывает догадку", "Артикул 202-555-0147", ControlMode.Strict, "202-555-0147");

            PhoneValue("число после номера не входит в него", "тел. 8 916 123 45 67 2 шт", "8 916 123 45 67");
            PhoneValue("число после короткого номера", "тел. 123-45-67 2 шт", "123-45-67");
            PhoneValue("скобки вокруг номера", "(8 916 123-45-67)", "8 916 123-45-67");
            PhoneValue("скобка кода входит в номер", "Офис (495) 123-45-67.", "(495) 123-45-67");
            PhoneReason("причина с подсказкой", "тел. 22-33-44", "после «тел.»");
            PhoneConfidence("+7 надёжен", "+7 916 123-45-67", Confidence.High);
            PhoneConfidence("слитный номер вероятен", "89161234567", Confidence.Medium);
            PhoneConfidence("подсказка делает надёжным", "Телефон 89161234567", Confidence.High);

            PhoneConfidence("номер через точки вероятен", "8.916.123.45.67", Confidence.Medium);
            PhoneConfidence("call как подсказка", "call me at (202) 555-0147", Confidence.High);

            NoType("дата и время", "2026-09-28 10:00:00 и 28.09.2026", "PHONE");
            NoType("дата в diff", "+2026-09-28 10:00 запуск", "PHONE");
            NoType("IP похожий на номер", "Узел 85.143.220.111 доступен", "PHONE");
            NoType("версия", "build 10.0.19041.1234", "PHONE");
            NoType("номер карты", "Карта 4276 3800 1234 5678", "PHONE");
            NoType("СНИЛС", "СНИЛС 123-456-789 01", "PHONE");
            NoType("ИНН", "ИНН 7707083893", "PHONE");
            NoType("ОГРН", "ОГРН 1027700132195", "PHONE");
            NoType("счёт", "р/с 40817810099910004312", "PHONE");
            NoType("время в миллисекундах", "ts=1727500000000", "PHONE");
            NoType("сумма с пробелами", "Итого 1 000 000 руб.", "PHONE");
            NoType("цена", "Цена 8 900 руб.", "PHONE");
            NoType("код из смс", "Код из смс 123456", "PHONE");
            NoType("короткое число после контакта", "контакт 12345 в CRM", "PHONE");
            NoType("идентификатор с дефисом", "ID-89161234567", "PHONE");
            NoType("часть длинного числа", "123-89161234567", "PHONE");
            NoType("одиночные цифры", "8 9 3 4 5 6 7 8 9 1 2", "PHONE");
            NoType("метка", "[PHONE_1] и [IP_2]", "PHONE");
        }

        private static Detection FindPhone(string text)
        {
            foreach (Detection detection in Detector.Scan(text, null, ControlMode.Balanced, false))
            {
                if (detection.Type == "PHONE")
                {
                    return detection;
                }
            }
            return null;
        }

        private static void PhoneValue(string name, string text, string expected)
        {
            Detection phone = FindPhone(text);
            Check(name, phone != null && phone.Value == expected, phone == null ? "нет находки" : phone.Value);
        }

        private static void PhoneReason(string name, string text, string expected)
        {
            Detection phone = FindPhone(text);
            Check(name, phone != null && phone.Reason == expected, phone == null ? "нет находки" : phone.Reason);
        }

        private static void PhoneConfidence(string name, string text, Confidence expected)
        {
            Detection phone = FindPhone(text);
            Check(name, phone != null && phone.Confidence == expected,
                phone == null ? "нет находки" : phone.Confidence.ToString());
        }
    }
}
