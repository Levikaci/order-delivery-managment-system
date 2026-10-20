# Правила системы контроля версий

## Репозиторий

Фактическое имя GitHub-репозитория:

```text
Levikaci/order-delivery-managment-system
```

В названии репозитория уже существует историческая опечатка `managment`; переименовывать репозиторий без необходимости не следует, чтобы не ломать существующие ссылки.

## Ветки

Основные:

```text
main
develop
feature/*
fix/*
docs/*
```

Для Этапа 1 используется:

```text
docs/project-artifacts
```

## Правила коммитов

Один коммит = одно логически завершенное изменение.

Используются:

```text
feat: ...
fix: ...
docs: ...
test: ...
refactor: ...
chore: ...
```

Не использовать:

```text
update
work
123
fix
изменения
```

## Требования Этапа 2

До сдачи Этапа 2 необходимо иметь минимум 5 осмысленных коммитов.

Рекомендуемая последовательность:

```text
1. chore: add gitignore and editorconfig
2. docs: add project scope and modules
3. docs: add architecture and dependencies
4. docs: add contracts and testing strategy
5. docs: add version control rules and ADRs
```

Затем отдельным коммитом можно зафиксировать отчет Этапа 2.

## Учебный конфликт

Конфликт должен быть реальным, а не нарисованным в отчете.

Пример:

```bash
git checkout main
git checkout -b conflict/base
printf "delivery-status=Created\n" > docs/conflict-demo.txt
git add docs/conflict-demo.txt
git commit -m "test: add conflict demo base"

git checkout -b conflict/change-a
printf "delivery-status=Assigned\n" > docs/conflict-demo.txt
git add docs/conflict-demo.txt
git commit -m "test: change delivery status in branch a"

git checkout main
git checkout -b conflict/change-b
printf "delivery-status=Cancelled\n" > docs/conflict-demo.txt
git add docs/conflict-demo.txt
git commit -m "test: change delivery status in branch b"

git checkout conflict/change-a
git merge conflict/change-b
```

После появления конфликта его необходимо разрешить вручную, затем:

```bash
git add docs/conflict-demo.txt
git commit -m "test: resolve merge conflict"
```

После проверки ветки можно удалить:

```bash
git branch -d conflict/change-a
git branch -d conflict/change-b
git branch -d conflict/base
```

## Тег

После завершения этапа:

```bash
git tag -a v0.1.0 -m "Coursework stage 2"
git push origin v0.1.0
```

## Финальная проверка

```bash
git status
git branch
git log --oneline --graph --decorate --all
git tag
```

В отчете нельзя писать, что конфликт, тег или команды выполнены, пока они фактически не выполнены.
