# DigLik.JobScheduler

![NuGet Version](https://img.shields.io/nuget/v/DigLik.JobScheduler)
![License](https://img.shields.io/github/license/DigLik/JobScheduler)

Экстремально быстрый, **zero-allocation** (без выделения памяти) и lock-free планировщик задач для .NET 10. 
Разработан специально для игровых движков, физических симуляций и высоконагруженных систем (HFT), где паузы сборщика мусора (GC) недопустимы.

## Особенности
* 🚀 **Обходит `Parallel.For`**: За счет умного авто-батчинга и адаптивного ожидания потоков.
* 🗑️ **100% Zero Allocation**: 0 байт выделенной памяти и 0 срабатываний GC во время работы планировщика. Никакого boxing'а и аллокаций делегатов.
* 🧠 **Lock-Free Архитектура**: В основе лежат алгоритмы *ABA-safe Treiber Stack* и очередь кражи работы *Chase-Lev Work-Stealing Deque*.
* 🔌 **Чистый и безопасный API**: Просто передайте свои данные как `struct` (без использования unsafe-кода), а планировщик сам всё оптимизирует.

## Установка

Установите пакет через NuGet Package Manager:
```bash
dotnet add package DigLik.JobScheduler
```

## Использование

### 1. Параллельная обработка массивов (Авто-батчинг)
Планировщик автоматически разрезает массив на оптимальные чанки (блоки) в зависимости от количества ядер вашего процессора для идеальной балансировки нагрузки.

```csharp
using JobScheduler;

// 1. Определяем задачу как структуру (struct)
public struct ProcessArrayJob : IJob
{
    public double[] Data;

    public void Execute(int index)
    {
        // Параметр index указывает на текущий элемент массива
        Data[index] = Math.Sqrt(Data[index]);
    }
}

// 2. Инициализируем планировщик (обычно один раз на всё приложение)
using var scheduler = new Scheduler();

var data = new double[100_000];
var job = new ProcessArrayJob { Data = data };

// 3. Планируем задачу на весь массив и ждем завершения
var handle = scheduler.Schedule(in job, data.Length);
scheduler.Wait(handle);
```

### 2. Одиночные задачи и графы зависимостей
Для одиночных задач параметр `index` в методе `Execute` всегда будет равен `0`.

```csharp
public struct MathJob : IJob
{
    public void Execute(int index) 
    {
        // Сложные математические вычисления...
    }
}

var jobA = new MathJob();
var jobB = new MathJob();

// jobB автоматически дождется завершения jobA перед запуском
var handleA = scheduler.Schedule(in jobA);
var handleB = scheduler.Schedule(in jobB, dependency: handleA);

scheduler.Wait(handleB); // Ждем выполнения всей цепочки
```

## Производительность (Бенчмарки)
Тестирование проводилось на процессоре Intel Core Ultra 9 285K (.NET 10.0.5).

| Метод | Кол-во задач | Время (Mean) | Ratio | Выделено памяти (Allocated) |
|---|---:|---:|---:|
| **DigLik.JobScheduler (Наш)** | **500** | **11.12 us** | **0.80** | **- (0 B)** |
| StandardParallelFor | 500 | 13.93 us |  1.00 | 5706 B |
| StandardTasksWhenAll | 500 | 147.57 us |  10.59 | 36205 B |

*`DigLik.JobScheduler` не только обходит стандартный `Parallel.For` по скорости на средних и больших объемах, но и гарантирует абсолютное отсутствие мусора в куче (Heap).*